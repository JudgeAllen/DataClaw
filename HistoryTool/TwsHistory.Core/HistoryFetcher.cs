using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using IBApi;

namespace TwsHistory.Core
{
    /// <summary>Outcome of a fetch.</summary>
    public sealed class FetchResult
    {
        public bool Success => Error == null && !Cancelled;
        public bool Cancelled;
        public string Error;
        public List<BarRecord> Bars = new List<BarRecord>();
        public int Pages;
    }

    /// <summary>
    /// Connects to TWS / IB Gateway and fetches historical bars, paginating
    /// backwards automatically until the requested start time is reached, a
    /// page comes back empty, or no progress is made. All console output is
    /// routed through the <c>info</c> / <c>error</c> callbacks so the same code
    /// serves the CLI tool and the GUI.
    /// </summary>
    public sealed class HistoryFetcher
    {
        private const int ReqId = 1001;

        private readonly CliOptions opt;
        private readonly Action<string> info;
        private readonly Action<string> error;
        private TwsWrapper wrapper;
        private EClientSocket client;
        private string loginTzName; // e.g. "China Standard Time", from the server handshake

        public HistoryFetcher(CliOptions opt, Action<string> info, Action<string> error)
        {
            this.opt = opt;
            this.info = info ?? (_ => { });
            this.error = error ?? (_ => { });
        }

        public FetchResult Fetch(CancellationToken ct = default)
        {
            var result = new FetchResult();
            using var conn = new TwsConnection(opt, msg => error(msg));
            wrapper = conn.Wrapper;

            try
            {
                if (!conn.Connect(opt, info))
                {
                    result.Error = "Connection failed: " + wrapper.LastErrorText;
                    return result;
                }
                client = conn.Client; // created by Connect()

                loginTzName = ParseLoginTz(client.ServerTime);

                if (opt.UseDelayed)
                {
                    info("Using delayed data (reqMarketDataType 3).");
                    client.reqMarketDataType(3);
                }

                if (ct.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return result;
                }

                result.Bars = FetchAllBars(ct, result);
                return result;
            }
            catch (Exception ex)
            {
                error("exception: " + ex);
                result.Error = ex.Message;
                return result;
            }
        }

        // ------------------------------------------------------------ data fetch

        private List<BarRecord> FetchAllBars(CancellationToken ct, FetchResult result)
        {
            var all = new Dictionary<string, BarRecord>();
            DateTime? targetStart = ComputeTargetStart();
            string endDateTime = NormalizeEnd(opt.End);
            string prevEarliestKey = null;
            int pages = 0;
            bool delayedRetried = false;
            int shrinkCount = 0;
            int seq = 0;
            bool exchangeRetried = false;
            Contract contract = BuildContract();

            // With an explicit start, the page window is the requested range capped
            // by the user's duration: changing the duration visibly changes the
            // pagination step, while the range still prevents unbounded over-fetch.
            string durationStr = opt.Duration;
            if (!string.IsNullOrWhiteSpace(opt.Start))
            {
                try
                {
                    DateTime startWall = TimeUtil.Parse(opt.Start);
                    DateTime endWall = string.IsNullOrWhiteSpace(opt.End) ? NowInLoginTz() : TimeUtil.Parse(opt.End);
                    double rangeDays = Math.Max(1, (endWall - startWall).TotalDays);
                    double userDays = DurationToDays(durationStr);
                    if (userDays >= 1)
                        durationStr = Math.Ceiling(Math.Min(userDays, rangeDays)).ToString(CultureInfo.InvariantCulture) + " D";
                    // sub-day durations (e.g. "30 S") or unparseable ones are kept verbatim
                    info($"Requested range {startWall:yyyy-MM-dd HH:mm} .. {endWall:yyyy-MM-dd HH:mm} -> page duration {durationStr}");
                }
                catch
                {
                    durationStr = opt.Duration;
                }
            }

            while (pages < opt.MaxPages)
            {
                pages++;
                int reqId = ReqId + (seq++); // unique per attempt: late responses from a
                                             // timed-out request must not pollute a retry
                info(
                    $"Page {pages}: end={(endDateTime.Length == 0 ? "(now)" : endDateTime)} " +
                    $"duration={durationStr} barSize={opt.BarSize} whatToShow={opt.WhatToShow} useRTH={opt.UseRth}");

                wrapper.ResetRequest(reqId);
                client.reqHistoricalData(reqId, contract, endDateTime, durationStr, opt.BarSize,
                    opt.WhatToShow, opt.UseRth, 1 /* formatDate */, false, null);

                var page = wrapper.WaitForHistoricalDataEnd(TimeSpan.FromSeconds(opt.RequestTimeoutSec), ct);
                if (page == null)
                {
                    try { client.cancelHistoricalData(reqId); } catch { /* ignore */ }
                    if (shrinkCount < 4)
                    {
                        string smaller = HalveDuration(durationStr);
                        if (smaller != null)
                        {
                            shrinkCount++;
                            durationStr = smaller;
                            info($"Page {pages}: timed out after {opt.RequestTimeoutSec}s; retrying with duration {durationStr} ...");
                            continue;
                        }
                    }
                    error($"Page {pages}: timed out after {opt.RequestTimeoutSec}s, cancelling request.");
                    result.Error = $"Page {pages} timed out after {opt.RequestTimeoutSec}s.";
                    return all.Values.OrderBy(b => b.Time).ToList();
                }
                if (page.Cancelled)
                {
                    result.Cancelled = true;
                    try { client.cancelHistoricalData(reqId); } catch { /* ignore */ }
                    info("Fetch cancelled.");
                    return all.Values.OrderBy(b => b.Time).ToList();
                }
                if (page.Error != null)
                {
                    // code 162 "HMDS query returned no data" on a later page means we
                    // have reached the end of the available data - keep what we got.
                    if (page.Error.Contains("returned no data", StringComparison.OrdinalIgnoreCase))
                    {
                        info($"End of available data reached ({page.Error}).");
                        break;
                    }
                    // "No security definition" on a futures contract: some TWS routes
                    // (e.g. China) know the contract under a different exchange name
                    // (GLOBEX <-> CME, NYMEX <-> COMEX). Retry with the alternate.
                    if (page.Error.Contains("No security definition", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(opt.SecType, "FUT", StringComparison.OrdinalIgnoreCase)
                        && !exchangeRetried)
                    {
                        string alt = AltExchange(contract.Exchange);
                        if (alt != null)
                        {
                            exchangeRetried = true;
                            info($"No security definition on {contract.Exchange}; retrying with {alt} ...");
                            contract = BuildContractWithExchange(alt);
                            continue;
                        }
                    }
                    // code 165: HMDS disconnect - the request window is too large;
                    // retry with a smaller duration instead of failing outright.
                    if (page.Error.StartsWith("code 165", StringComparison.Ordinal) && shrinkCount < 4)
                    {
                        string smaller = HalveDuration(durationStr);
                        if (smaller != null)
                        {
                            shrinkCount++;
                            durationStr = smaller;
                            info($"Request too large (code 165); retrying with duration {durationStr} ...");
                            continue;
                        }
                    }
                    error($"Page {pages}: request failed ({page.Error}).");
                    result.Error = page.Error;
                    return all.Values.OrderBy(b => b.Time).ToList();
                }

                int newBars = 0;
                foreach (var b in page.Bars)
                {
                    string key = b.Time.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture);
                    if (!all.ContainsKey(key))
                    {
                        all[key] = b;
                        newBars++;
                    }
                }
                info($"  received {page.Bars.Count} bars ({newBars} new)" +
                    (page.EarliestTimeText != null ? $", earliest = {page.EarliestTimeText}" : "") +
                    (page.DroppedBars > 0 ? $"; {page.DroppedBars} bars skipped (empty or unparseable time)" : "") +
                    (page.Bars.Count == 0 && page.ServerStart != null
                        ? $"; server says data range: {page.ServerStart} .. {page.ServerEnd}"
                        : ""));

                if (page.Bars.Count == 0)
                {
                    // Accounts without real-time market data get empty responses in
                    // real-time mode; retry once with the delayed feed.
                    if (pages == 1 && !opt.UseDelayed && !delayedRetried)
                    {
                        delayedRetried = true;
                        info("No bars in real-time mode; retrying with delayed data (reqMarketDataType 3) ...");
                        try { client.reqMarketDataType(3); } catch { /* ignore */ }
                        continue;
                    }
                    break;
                }
                if (prevEarliestKey != null && newBars == 0 && page.EarliestKey == prevEarliestKey) break;

                if (targetStart.HasValue && page.EarliestTime <= targetStart.Value)
                {
                    info($"  reached target start {targetStart.Value:yyyy-MM-dd HH:mm:ss}");
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    info("Fetch cancelled.");
                    break;
                }

                prevEarliestKey = page.EarliestKey;
                endDateTime = ToGmtEnd(page.EarliestTime.Value, page.EarliestTimeText);
            }

            if (pages >= opt.MaxPages)
                info($"Reached --max-pages ({opt.MaxPages}); the result may be incomplete.");
            result.Pages = pages;

            var bars = all.Values.OrderBy(b => b.Time).ToList();
            bars = TrimToRange(bars);
            return bars;
        }

        /// <summary>
        /// Drops bars outside the explicitly requested [start, end] range.
        /// Bar timestamps and the user's start/end are both wall-clock times in
        /// the TWS login timezone, so the comparison is direct.
        /// </summary>
        private List<BarRecord> TrimToRange(List<BarRecord> bars)
        {
            DateTime? startWall = null;
            DateTime? endWall = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(opt.Start)) startWall = TimeUtil.Parse(opt.Start);
                if (!string.IsNullOrWhiteSpace(opt.End)) endWall = TimeUtil.Parse(opt.End);
            }
            catch
            {
                return bars;
            }
            if (!startWall.HasValue && !endWall.HasValue) return bars;

            var kept = new List<BarRecord>();
            int trimmed = 0;
            foreach (var b in bars)
            {
                if (startWall.HasValue && b.Time < startWall.Value) { trimmed++; continue; }
                if (endWall.HasValue && b.Time > endWall.Value) { trimmed++; continue; }
                kept.Add(b);
            }
            if (trimmed > 0)
                info($"Trimmed {trimmed} bars outside the requested range.");
            return kept;
        }

        /// <summary>
        /// The pagination stop time: explicit --start, otherwise derived from
        /// --duration applied to --end (or now). Returns null if it cannot be computed.
        /// </summary>
        private DateTime? ComputeTargetStart()
        {
            if (!string.IsNullOrWhiteSpace(opt.Start))
            {
                try { return TimeUtil.Parse(opt.Start); }
                catch (FormatException ex) { throw new ArgumentException("Invalid --start: " + ex.Message); }
            }

            var m = Regex.Match(opt.Duration.Trim(), @"^(\d+(?:\.\d+)?)\s*([SDWMY])$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            char unit = char.ToUpperInvariant(m.Groups[2].Value[0]);

            DateTime end;
            if (string.IsNullOrWhiteSpace(opt.End))
                end = NowInLoginTz();
            else
            {
                try { end = TimeUtil.Parse(opt.End); }
                catch (FormatException ex) { throw new ArgumentException("Invalid --end: " + ex.Message); }
            }

            return unit switch
            {
                'S' => end.AddSeconds(-n),
                'D' => end.AddDays(-n),
                'W' => end.AddDays(-7 * n),
                'M' => end.AddMonths(-(int)Math.Round(n)),
                'Y' => end.AddYears(-(int)Math.Round(n)),
                _ => end
            };
        }

        /// <summary>
        /// Converts a bare "yyyyMMdd HH:mm:ss" end time into an absolute request
        /// end: the wall time is interpreted in the TWS login timezone (the same
        /// basis as bar timestamps) and anchored on UTC. Falls back to appending
        /// --tz if the login timezone is unknown; leaves "" (now) and explicit
        /// timezone-bearing strings untouched.
        /// </summary>
        private string NormalizeEnd(string end)
        {
            if (string.IsNullOrWhiteSpace(end)) return "";
            if (!Regex.IsMatch(end, @"^\d{8}( \d{2}:\d{2}(:\d{2})?)?$"))
                return end;

            if (loginTzName != null)
            {
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(loginTzName);
                    DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(TimeUtil.Parse(end), DateTimeKind.Unspecified), zone);
                    return utc.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
                }
                catch
                {
                    // fall through to the fallback below
                }
            }
            return end.Trim() + " " + opt.Timezone;
        }

        /// <summary>Current wall-clock time in the TWS login timezone (bar time basis).</summary>
        private DateTime NowInLoginTz()
        {
            if (loginTzName != null)
            {
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(loginTzName);
                    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
                }
                catch { /* fall through */ }
            }
            return DateTime.UtcNow;
        }

        /// <summary>Converts a "N S/D/W/M/Y" duration to days (approximate); -1 if unparseable.</summary>
        private static double DurationToDays(string duration)
        {
            var m = Regex.Match(duration.Trim(), @"^(\d+(?:\.\d+)?)\s*([SDWMY])$", RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return char.ToUpperInvariant(m.Groups[2].Value[0]) switch
            {
                'S' => n / 86400.0,
                'D' => n,
                'W' => n * 7,
                'M' => n * 30.44,
                'Y' => n * 365.25,
                _ => -1
            };
        }

        /// <summary>Halves a "N S/D/W/M/Y" duration string (minimum 1).</summary>
        private static string HalveDuration(string duration)
        {
            var m = Regex.Match(duration.Trim(), @"^(\d+(?:\.\d+)?)\s*([SDWMY])$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int half = Math.Max(1, (int)Math.Ceiling(n / 2));
            return half + " " + m.Groups[2].Value.ToUpperInvariant();
        }

        /// <summary>
        /// Extracts the TWS login timezone from the server time string,
        /// e.g. "20260814 22:19:05 China Standard Time" -> "China Standard Time".
        /// Bar timestamps are wall-clock times in this timezone.
        /// </summary>
        private static string ParseLoginTz(string serverTime)
        {
            if (string.IsNullOrWhiteSpace(serverTime)) return null;
            var m = Regex.Match(serverTime.Trim(), @"^\d{8} \d{2}:\d{2}:\d{2} (.+)$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>
        /// Converts a bar's wall-clock time (TWS login timezone) into an absolute
        /// "yyyyMMdd HH:mm:ss UTC" end time for the next pagination request. Using
        /// UTC anchors makes pagination exact regardless of the login timezone or
        /// --tz. Note: newer TWS rejects "GMT" (10314) but accepts "UTC".
        /// Falls back to the plain wall time + --tz if conversion is impossible.
        /// </summary>
        private string ToGmtEnd(DateTime wallTime, string wallText)
        {
            if (loginTzName != null)
            {
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(loginTzName);
                    DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified), zone);
                    return utc.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
                }
                catch
                {
                    // fall through to the fallback below
                }
            }
            return wallText + " " + opt.Timezone;
        }

        private Contract BuildContract()
        {
            return BuildContractWithExchange(opt.Exchange);
        }

        private Contract BuildContractWithExchange(string exchange)
        {
            return new Contract
            {
                ConId = opt.ConId,
                Symbol = opt.Symbol,
                SecType = opt.SecType,
                Exchange = exchange,
                Currency = opt.Currency,
                PrimaryExch = opt.PrimaryExch,
                LocalSymbol = opt.LocalSymbol,
                LastTradeDateOrContractMonth = opt.LastTradeDate,
                Right = opt.Right,
                Strike = opt.Strike,
                Multiplier = opt.Multiplier,
                TradingClass = opt.TradingClass,
                IncludeExpired = opt.IncludeExpired
            };
        }

        /// <summary>Alternate exchange name for the same product on some TWS routes.</summary>
        private static string AltExchange(string exchange)
        {
            switch (exchange?.ToUpperInvariant())
            {
                case "GLOBEX": return "CME";
                case "CME": return "GLOBEX";
                case "NYMEX": return "COMEX";
                case "COMEX": return "NYMEX";
                default: return null;
            }
        }
    }
}
