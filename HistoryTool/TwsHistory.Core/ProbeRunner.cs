using System;
using System.Linq;
using System.Threading;
using IBApi;

namespace TwsHistory.Core
{
    /// <summary>
    /// Diagnostic probes used to isolate why historical data comes back empty:
    /// real-time market data type, historical ticks, and keepUpToDate bars.
    /// </summary>
    public static class ProbeRunner
    {
        private const int ProbeReqId = 2001;

        public static int ProbeMkt(CliOptions opt, Action<string> info, Action<string> error, bool forceDelayed = false)
        {
            using var conn = new TwsConnection(opt, error);
            if (!conn.Connect(opt, info)) return 1;

            var contract = BuildContract(opt);
            if (forceDelayed)
            {
                info("Forcing marketDataType = 3 (delayed) ...");
                conn.Client.reqMarketDataType(3);
            }
            info($"Requesting market data for {opt.Symbol} ...");
            conn.Client.reqMktData(ProbeReqId, contract, "", false, false, null);

            Thread.Sleep(6000);
            conn.Client.cancelMktData(ProbeReqId);

            var p = conn.Wrapper.Probe;
            string mdType = p.MarketDataType switch
            {
                1 => "real-time",
                2 => "frozen",
                3 => "delayed",
                4 => "delayed-frozen",
                _ => "unknown(" + p.MarketDataType + ")"
            };
            info($"marketDataType = {mdType}");
            info(p.HasLastPrice ? $"last price tick = {p.LastPrice}" : "no price tick received");
            return 0;
        }

        public static int ProbeTicks(CliOptions opt, Action<string> info, Action<string> error)
        {
            using var conn = new TwsConnection(opt, error);
            if (!conn.Connect(opt, info)) return 1;

            var contract = BuildContract(opt);
            info($"Requesting last 100 historical ticks for {opt.Symbol} ...");
            conn.Client.reqHistoricalTicks(ProbeReqId, contract, "", "", 100, "TRADES", opt.UseRth, false, null);

            conn.Wrapper.Probe.Wake.WaitOne(15000);
            var ticks = conn.Wrapper.Probe.Ticks;
            if (ticks.Count == 0)
            {
                info("0 ticks received.");
                return 0;
            }
            var first = ticks[0];
            var last = ticks[ticks.Count - 1];
            info($"{ticks.Count} ticks received: first @{UnixToUtc(first.Time):yyyy-MM-dd HH:mm:ss} price={first.Price}, last @{UnixToUtc(last.Time):yyyy-MM-dd HH:mm:ss} price={last.Price}");
            return 0;
        }

        public static int ProbeKeepUpToDate(CliOptions opt, Action<string> info, Action<string> error)
        {
            using var conn = new TwsConnection(opt, error);
            if (!conn.Connect(opt, info)) return 1;

            var contract = BuildContract(opt);
            info($"Requesting keepUpToDate=true {opt.BarSize} bars for {opt.Symbol} (streaming 10s) ...");
            conn.Wrapper.ResetRequest(ProbeReqId);
            conn.Client.reqHistoricalData(ProbeReqId, contract, "", opt.Duration, opt.BarSize,
                opt.WhatToShow, opt.UseRth, 1, true /* keepUpToDate */, null);

            Thread.Sleep(10000);
            conn.Client.cancelHistoricalData(ProbeReqId);

            var bars = conn.Wrapper.TakeCurrentBars();
            info(bars.Count == 0
                ? "0 bars streamed."
                : $"{bars.Count} bars streamed, range {bars.Min(b => b.Time):yyyy-MM-dd HH:mm:ss} .. {bars.Max(b => b.Time):yyyy-MM-dd HH:mm:ss}");
            return 0;
        }

        public static int ProbeConid(CliOptions opt, Action<string> info, Action<string> error)
        {
            using var conn = new TwsConnection(opt, error);
            if (!conn.Connect(opt, info)) return 1;

            var contract = BuildContract(opt);
            info($"Requesting contract details for {opt.Symbol} ...");
            conn.Client.reqContractDetails(ProbeReqId, contract);

            conn.Wrapper.Probe.Wake.WaitOne(10000);
            Thread.Sleep(500);
            var details = conn.Wrapper.Probe.ContractDetails;
            if (details.Count == 0)
            {
                info("no contract details received.");
                return 0;
            }
            foreach (var d in details)
            {
                var c = d.Contract;
                info($"conId={c.ConId} symbol={c.Symbol} secType={c.SecType} exchange={c.Exchange} primaryExch={c.PrimaryExch} " +
                     $"currency={c.Currency} localSymbol={c.LocalSymbol} tradingClass={c.TradingClass} lastTradeDate={c.LastTradeDateOrContractMonth} " +
                     $"longName={d.LongName} validExchanges={d.ValidExchanges}");
            }
            return 0;
        }

        public static int ProbeHead(CliOptions opt, Action<string> info, Action<string> error)
        {
            using var conn = new TwsConnection(opt, error);
            if (!conn.Connect(opt, info)) return 1;

            if (opt.UseDelayed)
            {
                info("Using delayed data (reqMarketDataType 3).");
                conn.Client.reqMarketDataType(3);
            }

            var contract = BuildContract(opt);
            info($"Requesting head timestamp for {opt.Symbol} {opt.BarSize} ...");
            conn.Client.reqHeadTimestamp(ProbeReqId, contract, opt.WhatToShow, opt.UseRth, 1);

            conn.Wrapper.Probe.Wake.WaitOne(10000);
            var head = conn.Wrapper.Probe.HeadTimestamp;
            info(string.IsNullOrEmpty(head) ? "no head timestamp received." : $"head timestamp = {head}");
            return 0;
        }

        public static int ProbeMatch(CliOptions opt, Action<string> info, Action<string> error)
        {
            using var conn = new TwsConnection(opt, error);
            if (!conn.Connect(opt, info)) return 1;

            info($"Searching symbols matching '{opt.Symbol}' ...");
            conn.Client.reqMatchingSymbols(ProbeReqId, opt.Symbol);

            conn.Wrapper.Probe.Wake.WaitOne(10000);
            Thread.Sleep(300);
            var samples = conn.Wrapper.Probe.SymbolSamples;
            if (samples.Count == 0)
            {
                info("no matching symbols returned.");
                return 0;
            }
            int shown = 0;
            foreach (var s in samples)
            {
                if (shown >= 15) break;
                shown++;
                var c = s.Contract;
                info($"  {c.Symbol} | {c.SecType} | {c.Exchange} | {c.Currency} | conId={c.ConId} | lastTrade={c.LastTradeDateOrContractMonth} | local={c.LocalSymbol} | {c.Description}");
            }
            info($"(showing {shown} of {samples.Count})");
            return 0;
        }

        private static Contract BuildContract(CliOptions opt)
        {
            return new Contract
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
        }

        private static DateTime UnixToUtc(long seconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
    }
}
