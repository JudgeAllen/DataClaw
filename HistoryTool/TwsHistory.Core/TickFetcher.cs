using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using IBApi;

namespace TwsHistory.Core
{
    /// <summary>A single historical trade tick (time converted to TWS-login-timezone wall clock).</summary>
    public sealed class TickRecord
    {
        public DateTime Time;
        public double Price;
        public decimal Size;
    }

    public sealed class TickResult
    {
        public bool Success => Error == null && !Cancelled;
        public bool Cancelled;
        public string Error;
        public List<TickRecord> Ticks = new List<TickRecord>();
        public int Requests;
    }

    /// <summary>
    /// Fetches historical trade ticks via reqHistoricalTicks (max 1000 per request),
    /// paginating backwards until the requested start time is reached or data ends.
    /// NOTE: tick volume is huge - expect ~1000 ticks per request and heavy pacing
    /// limits (60 requests / 10 min per client id), so only short ranges are practical.
    /// </summary>
    public sealed class TickFetcher
    {
        private const int ReqIdBase = 3001;
        private const int MaxTicksPerRequest = 1000;

        private readonly CliOptions opt;
        private readonly Action<string> info;
        private readonly Action<string> error;
        private string loginTzName;

        public TickFetcher(CliOptions opt, Action<string> info, Action<string> error)
        {
            this.opt = opt;
            this.info = info ?? (_ => { });
            this.error = error ?? (_ => { });
        }

        public TickResult Fetch(CancellationToken ct = default)
        {
            var result = new TickResult();
            using var conn = new TwsConnection(opt, msg => error(msg));
            var wrapper = conn.Wrapper;

            try
            {
                if (!conn.Connect(opt, info))
                {
                    result.Error = "Connection failed: " + wrapper.LastErrorText;
                    return result;
                }
                var client = conn.Client; // created by Connect()
                loginTzName = ParseLoginTz(client.ServerTime);

                var contract = new Contract
                {
                    ConId = opt.ConId,
                    Symbol = opt.Symbol,
                    SecType = opt.SecType,
                    Exchange = opt.Exchange,
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

                long startEpoch = WallToUtcEpoch(TimeUtil.Parse(opt.Start));
                long endEpoch = string.IsNullOrWhiteSpace(opt.End)
                    ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    : WallToUtcEpoch(TimeUtil.Parse(opt.End));
                if (endEpoch <= startEpoch)
                    throw new ArgumentException("--end must be after --start");

                // multiple trades can share the same second; dedupe on the full triple
                var seen = new HashSet<(long, double, decimal)>();
                var all = new List<(long time, double price, decimal size)>();
                int seq = 0;

                info($"Tick fetch: {EpochToTicksString(startEpoch)} .. {EpochToTicksString(endEpoch)}, " +
                     $"up to {MaxTicksPerRequest} ticks/request (whatToShow={opt.WhatToShow}) - duration is ignored in ticks mode.");

                // The server returns the OLDEST ticks of the window, so paginate FORWARD.
                while (result.Requests < opt.MaxPages)
                {
                    result.Requests++;
                    int reqId = ReqIdBase + (seq++);
                    string startStr = EpochToTicksString(startEpoch);
                    info($"Tick request {result.Requests}: {startStr} .. {EpochToTicksString(endEpoch)}");

                    wrapper.ResetTicksRequest(reqId);
                    client.reqHistoricalTicks(reqId, contract, startStr, EpochToTicksString(endEpoch),
                        MaxTicksPerRequest, opt.WhatToShow, opt.UseRth, false, null);

                    var ticks = wrapper.WaitForTicksDone(TimeSpan.FromSeconds(opt.RequestTimeoutSec), ct);
                    if (ticks == null)
                    {
                        string tickErr = wrapper.LastTicksError;
                        if (!string.IsNullOrEmpty(tickErr))
                        {
                            error($"Tick request {result.Requests} failed ({tickErr}).");
                            result.Error = tickErr;
                        }
                        else
                        {
                            result.Error = $"Tick request {result.Requests} timed out or cancelled.";
                        }
                        break;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                    if (ticks.Count == 0)
                    {
                        info("  no more ticks in the window.");
                        break;
                    }

                    int added = 0;
                    foreach (var t in ticks)
                        if (seen.Add((t.Time, t.Price, t.Size)))
                        {
                            all.Add((t.Time, t.Price, t.Size));
                            added++;
                        }

                    info($"  {ticks.Count} ticks ({added} new), first={UtcToWall(ticks[0].Time):yyyy-MM-dd HH:mm:ss}, last={UtcToWall(ticks[ticks.Count - 1].Time):yyyy-MM-dd HH:mm:ss}");

                    if (ticks[ticks.Count - 1].Time >= endEpoch)
                    {
                        info("  reached requested end.");
                        break;
                    }
                    if (ticks.Count < MaxTicksPerRequest)
                    {
                        info("  window fully covered.");
                        break;
                    }

                    long newStart = ticks[ticks.Count - 1].Time; // inclusive: re-fetch the boundary second, dedupe drops it
                    if (newStart <= startEpoch) break; // safety: no forward progress
                    startEpoch = newStart;
                }

                if (result.Requests >= opt.MaxPages)
                    info($"Reached --max-pages ({opt.MaxPages}); the result may be incomplete.");

                foreach (var t in all.OrderBy(t => t.time))
                    result.Ticks.Add(new TickRecord { Time = UtcToWall(t.time), Price = t.price, Size = t.size });
                return result;
            }
            catch (Exception ex)
            {
                error("exception: " + ex);
                result.Error = ex.Message;
                return result;
            }
        }

        private long WallToUtcEpoch(DateTime wall)
        {
            if (loginTzName != null)
            {
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(loginTzName);
                    DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone);
                    return new DateTimeOffset(utc).ToUnixTimeSeconds();
                }
                catch { /* fall through */ }
            }
            return new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Local)).ToUnixTimeSeconds();
        }

        private DateTime UtcToWall(long epoch)
        {
            DateTime utc = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            if (loginTzName != null)
            {
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(loginTzName);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
                }
                catch { /* fall through */ }
            }
            return utc;
        }

        private static string EpochToTicksString(long epoch)
        {
            // server 223 accepts "yyyyMMdd HH:mm:ss UTC" (explicit timezone)
            return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                .ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        }

        private string EpochToUtcString(long epoch)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                .ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        }

        private static string ParseLoginTz(string serverTime)
        {
            if (string.IsNullOrWhiteSpace(serverTime)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(serverTime.Trim(), @"^\d{8} \d{2}:\d{2}:\d{2} (.+)$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
    }
}
