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

    /// <summary>A single historical bid/ask tick.</summary>
    public sealed class BidAskRecord
    {
        public DateTime Time; // the later of bid/ask time, in login-tz wall clock
        public double PriceBid;
        public double PriceAsk;
        public decimal SizeBid;
        public decimal SizeAsk;
    }

    public sealed class TickResult
    {
        public bool Success => Error == null && !Cancelled;
        public bool Cancelled;
        public string Error;
        public List<TickRecord> Ticks = new List<TickRecord>();
        public List<BidAskRecord> BidAsks = new List<BidAskRecord>();
        public int Requests;
    }

    /// <summary>
    /// Fetches historical ticks via reqHistoricalTicks (max 1000 per request),
    /// paginating forward (the server returns the OLDEST ticks of the window).
    /// whatToShow: TRADES/MIDPOINT -> trade ticks; BID_ASK -> bid/ask ticks.
    /// NOTE: tick volume is huge - use short windows (hours to a couple of days).
    /// </summary>
    public sealed class TickFetcher
    {
        private const int ReqIdBase = 3001;
        private int MaxTicksPerRequest => opt.MaxTicksPerRequest > 0 ? opt.MaxTicksPerRequest : 1000;

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

                bool isBidAsk = string.Equals(opt.WhatToShow, "BID_ASK", StringComparison.OrdinalIgnoreCase);

                info($"Tick fetch: {EpochToTicksString(startEpoch)} .. {EpochToTicksString(endEpoch)}, " +
                     $"up to {MaxTicksPerRequest} ticks/request (whatToShow={opt.WhatToShow}) - duration is ignored in ticks mode.");

                // The server returns the OLDEST ticks of the window, so paginate FORWARD.
                while (result.Requests < opt.MaxPages)
                {
                    result.Requests++;
                    int reqId = ReqIdBase + (result.Requests - 1);
                    string startStr = EpochToTicksString(startEpoch);
                    info($"Tick request {result.Requests}: {startStr} .. {EpochToTicksString(endEpoch)}");

                    wrapper.ResetTicksRequest(reqId);
                    client.reqHistoricalTicks(reqId, contract, startStr, EpochToTicksString(endEpoch),
                        MaxTicksPerRequest, opt.WhatToShow, opt.UseRth, false, null);

                    if (isBidAsk)
                    {
                        var batch = wrapper.WaitForTicksDoneBA(TimeSpan.FromSeconds(opt.RequestTimeoutSec), ct);
                        if (batch == null)
                        {
                            FailOrTimeout(result, result.Requests, wrapper);
                            break;
                        }
                        if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (batch.Count == 0) { info("  no more ticks in the window."); break; }

                        int added = 0;
                        foreach (var t in batch)
                            if (seenBA.Add((t.TimeBid, t.PriceBid, t.SizeBid, t.TimeAsk, t.PriceAsk, t.SizeAsk)))
                            {
                                allBA.Add(t);
                                added++;
                            }

                        long lastTime = Math.Max(batch[batch.Count - 1].TimeBid, batch[batch.Count - 1].TimeAsk);
                        info($"  {batch.Count} ticks ({added} new), first={UtcToWall(Math.Max(batch[0].TimeBid, batch[0].TimeAsk)):yyyy-MM-dd HH:mm:ss}, last={UtcToWall(lastTime):yyyy-MM-dd HH:mm:ss}");

                        if (lastTime >= endEpoch) { info("  reached requested end."); break; }
                        if (batch.Count < MaxTicksPerRequest) { info("  window fully covered."); break; }
                        if (lastTime <= startEpoch) break; // safety: no forward progress
                        startEpoch = lastTime;
                    }
                    else
                    {
                        var batch = wrapper.WaitForTicksDone(TimeSpan.FromSeconds(opt.RequestTimeoutSec), ct);
                        if (batch == null)
                        {
                            FailOrTimeout(result, result.Requests, wrapper);
                            break;
                        }
                        if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (batch.Count == 0) { info("  no more ticks in the window."); break; }

                        int added = 0;
                        foreach (var t in batch)
                            if (seenLast.Add((t.Time, t.Price, t.Size)))
                            {
                                allLast.Add((t.Time, t.Price, t.Size));
                                added++;
                            }

                        info($"  {batch.Count} ticks ({added} new), first={UtcToWall(batch[0].Time):yyyy-MM-dd HH:mm:ss}, last={UtcToWall(batch[batch.Count - 1].Time):yyyy-MM-dd HH:mm:ss}");

                        if (batch[batch.Count - 1].Time >= endEpoch) { info("  reached requested end."); break; }
                        if (batch.Count < MaxTicksPerRequest) { info("  window fully covered."); break; }
                        if (batch[batch.Count - 1].Time <= startEpoch) break; // safety: no forward progress
                        startEpoch = batch[batch.Count - 1].Time;
                    }
                }

                if (result.Requests >= opt.MaxPages)
                    info($"Reached --max-pages ({opt.MaxPages}); the result may be incomplete.");

                if (isBidAsk)
                {
                    foreach (var t in allBA.OrderBy(t => Math.Max(t.TimeBid, t.TimeAsk)))
                        result.BidAsks.Add(new BidAskRecord
                        {
                            Time = UtcToWall(Math.Max(t.TimeBid, t.TimeAsk)),
                            PriceBid = t.PriceBid,
                            PriceAsk = t.PriceAsk,
                            SizeBid = t.SizeBid,
                            SizeAsk = t.SizeAsk
                        });
                }
                else
                {
                    foreach (var t in allLast.OrderBy(t => t.time))
                        result.Ticks.Add(new TickRecord { Time = UtcToWall(t.time), Price = t.price, Size = t.size });
                }
                return result;
            }
            catch (Exception ex)
            {
                error("exception: " + ex);
                result.Error = ex.Message;
                return result;
            }
        }

        private readonly HashSet<(long, double, decimal)> seenLast = new HashSet<(long, double, decimal)>();
        private readonly List<(long time, double price, decimal size)> allLast = new List<(long time, double price, decimal size)>();
        private readonly HashSet<(long, double, decimal, long, double, decimal)> seenBA = new HashSet<(long, double, decimal, long, double, decimal)>();
        private readonly List<TwsWrapper.TickPointBA> allBA = new List<TwsWrapper.TickPointBA>();

        private void FailOrTimeout(TickResult result, int reqNo, TwsWrapper wrapper)
        {
            string tickErr = wrapper.LastTicksError;
            if (!string.IsNullOrEmpty(tickErr))
            {
                error($"Tick request {reqNo} failed ({tickErr}).");
                result.Error = tickErr;
            }
            else
            {
                result.Error = $"Tick request {reqNo} timed out or cancelled.";
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

        private static string ParseLoginTz(string serverTime)
        {
            if (string.IsNullOrWhiteSpace(serverTime)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(serverTime.Trim(), @"^\d{8} \d{2}:\d{2}:\d{2} (.+)$");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
    }
}
