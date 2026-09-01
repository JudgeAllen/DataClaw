using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using global::TwsHistory.Core;

namespace TwsHistory
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a == "--help" || a == "-h" || a == "/?")
                {
                    Console.WriteLine(Usage);
                    return 0;
                }
            }

            string probeMode = null;
            var filtered = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--probe-mkt" || args[i] == "--probe-mkt-d" || args[i] == "--probe-ticks" || args[i] == "--probe-kud" || args[i] == "--probe-conid" || args[i] == "--probe-head" || args[i] == "--probe-match")
                    probeMode = args[i].Substring(2);
                else
                    filtered.Add(args[i]);
            }

            CliOptions opt;
            try
            {
                opt = CliOptions.Parse(filtered.ToArray());
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                return 2;
            }

            try
            {
                Action<string> info = msg => Console.WriteLine(msg);
                Action<string> err = msg => Console.Error.WriteLine("[TWS] " + msg);
                if (probeMode != null)
                {
                    return probeMode switch
                    {
                        "probe-mkt" => ProbeRunner.ProbeMkt(opt, info, err),
                        "probe-mkt-d" => ProbeRunner.ProbeMkt(opt, info, err, forceDelayed: true),
                        "probe-ticks" => ProbeRunner.ProbeTicks(opt, info, err),
                        "probe-kud" => ProbeRunner.ProbeKeepUpToDate(opt, info, err),
                        "probe-conid" => ProbeRunner.ProbeConid(opt, info, err),
                        "probe-head" => ProbeRunner.ProbeHead(opt, info, err),
                        "probe-match" => ProbeRunner.ProbeMatch(opt, info, err),
                        _ => 2
                    };
                }
                if (opt.TicksMode)
                    return RunTicks(opt, info, err);
                return Run(opt, info, err);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal error: " + ex.Message);
                return 2;
            }
        }

        private static int RunTicks(CliOptions opt, Action<string> info, Action<string> error)
        {
            var fetcher = new TickFetcher(opt, info, error);
            var result = fetcher.Fetch();

            // Write whatever was collected even on partial failure/cancellation.
            if (result.Ticks.Count > 0)
            {
                string path = opt.Output ?? CsvExporter.DefaultTicksOutputName(opt);
                CsvExporter.WriteTicks(path, result.Ticks);
                Console.WriteLine();
                Console.WriteLine($"Ticks:    {result.Ticks.Count:N0} ({result.Requests} requests)");
                Console.WriteLine($"Range:    {result.Ticks[0].Time:yyyy-MM-dd HH:mm:ss}  ..  {result.Ticks[result.Ticks.Count - 1].Time:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"Output:   {Path.GetFullPath(path)}");
            }

            if (result.Error != null)
            {
                Console.Error.WriteLine("Failed: " + result.Error);
                return 1;
            }
            if (result.Cancelled)
            {
                Console.WriteLine("Cancelled (partial data written).");
                return 1;
            }
            if (result.Ticks.Count == 0)
            {
                Console.WriteLine("No ticks returned.");
                return 0;
            }
            return 0;
        }

        private static int Run(CliOptions opt, Action<string> info, Action<string> error)
        {
            var fetcher = new HistoryFetcher(opt, info, error);

            var result = fetcher.Fetch();

            if (result.Error != null)
            {
                Console.Error.WriteLine("Failed: " + result.Error);
                return 1;
            }
            if (result.Cancelled)
            {
                Console.WriteLine("Cancelled.");
                return 1;
            }
            if (result.Bars.Count == 0)
            {
                Console.WriteLine("No bars returned.");
                return 0;
            }

            string path = opt.Output ?? CsvExporter.DefaultOutputName(opt);
            CsvExporter.Write(path, result.Bars);

            Console.WriteLine();
            Console.WriteLine($"Bars:     {result.Bars.Count}");
            Console.WriteLine($"Range:    {result.Bars[0].Time:yyyy-MM-dd HH:mm:ss}  ..  {result.Bars[result.Bars.Count - 1].Time:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Output:   {Path.GetFullPath(path)}");
            return 0;
        }

        private static readonly string Usage =
@"TwsHistory - fetch historical bars from TWS / IB Gateway and write them to CSV.

Usage:  TwsHistory --symbol AAPL [options]
GUI:    TwsHistoryGui.exe

Required:
  --symbol <SYM>              instrument symbol

Contract options (defaults describe a plain stock):
  --sectype <T>               STK | OPT | FUT | CASH | IND | FOP | BAG | ... (default STK)
  --exchange <E>              (default SMART)
  --currency <C>              (default USD)
  --primary-exch <E>          native exchange, for ambiguous contracts
  --local-symbol <S>          exchange symbol, e.g. ""AAPL 260116C00275000""
  --last-trade-date <D>       YYYYMMDD or YYYYMM (options / futures)
  --right <C|P>               option right (options)
  --strike <N>                option strike (options)
  --multiplier <M>            contract multiplier (options / futures)
  --trading-class <T>         trading class, e.g. FGBL
  --include-expired           allow expired futures in historical queries

Historical data options:
  --duration <D>              ""30 S"" | ""1 D"" | ""6 M"" | ""1 Y"" ... (default ""1 D"")
  --bar-size <B>              ""1 secs"" | ""5 mins"" | ""1 hour"" | ""1 day"" ... (default ""1 day"")
                              ""ticks"" = tick 模式(等价 --ticks)
  --ticks                     fetch historical ticks instead of bars (requires --start;
                              whatToShow: TRADES default; max 1000 ticks/request)
  --what-to-show <W>          TRADES | MIDPOINT | BID | ASK | BID_ASK |
                              HISTORICAL_VOLATILITY | OPTION_IMPLIED_VOLATILITY | FEE_RATE
                              (default TRADES)
  --use-rth <0|1>             1 = regular trading hours only (default 0)
  --end <T>                   ending time ""yyyyMMdd HH:mm:ss"" (default: now)
  --start <T>                 stop paginating once bars reach this time
                              (default: now - duration)
  --tz <TZ>                   timezone fallback for pagination end times; Java-style
                              ids only, e.g. Asia/Shanghai, US/Eastern, UTC
                              (default Asia/Shanghai)
  --delayed <0|1>             force delayed data (reqMarketDataType 3) for accounts
                              without real-time subscriptions (auto-retried otherwise)
  --conid <N>                 contract id, bypasses symbol-based resolution

Diagnostic probes:
  --probe-mkt                 subscribe to market data briefly; prints data type/ticks
  --probe-mkt-d               same, with delayed data forced
  --probe-ticks               request last 100 historical ticks
  --probe-kud                 stream keepUpToDate bars for 10s
  --probe-head                request the head timestamp for the bar size
  --probe-conid               resolve the contract and print conId/details

Connection / output:
  --host <H>                  (default 127.0.0.1)
  --port <P>                  TWS: 7496 live / 7497 paper; Gateway: 4001 live / 4002 paper
                              (default 7497)
  --client-id <N>             (default 0)
  --output <FILE>             CSV output path (default <SYMBOL>_<SECTYPE>_<BARSIZE>_<WHAT>.csv)
  --max-pages <N>             safety cap on pagination requests (default 500)
  --connect-timeout <SEC>     (default 15)
  --request-timeout <SEC>     per-request timeout (default 120)

Notes:
  * TWS limits how far a single request reaches (depends on bar size). The tool
    paginates backwards automatically and de-duplicates boundary bars.
  * CSV timestamps are wall-clock times in the TWS login timezone, formatted
    ""yyyy-MM-dd HH:mm:ss"". For exact pagination alignment set --tz to the
    timezone you log into TWS with (Asia/Shanghai for CN markets).
  * In TWS: File > Global Configuration > API > Settings > check
    ""Enable ActiveX and Socket Clients"". Use a different --client-id than
    other running API clients.";
    }
}
