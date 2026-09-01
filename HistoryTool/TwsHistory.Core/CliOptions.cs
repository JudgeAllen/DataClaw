using System;
using System.Collections.Generic;
using System.Globalization;

namespace TwsHistory.Core
{
    /// <summary>All options for a historical-data fetch; shared by CLI and GUI.</summary>
    public sealed class CliOptions
    {
        public string Host = "127.0.0.1";
        public int Port = 7497;
        public int ClientId = 0;

        // contract
        public string Symbol;
        public int ConId; // bypass symbol-based resolution when known
        public string SecType = "STK";
        public string Exchange = "SMART";
        public string Currency = "USD";
        public string PrimaryExch;
        public string LocalSymbol;
        public string LastTradeDate;
        public string Right;
        public double Strike = 0;
        public string Multiplier;
        public string TradingClass;
        public bool IncludeExpired;

        // historical data
        public string Duration = "1 D";
        public string BarSize = "1 day";
        public string WhatToShow = "TRADES";
        public int UseRth = 0;
        public string End = "";                 // "" means "now"
        public string Start = "";               // "" means "now - duration"
        public string Timezone = "Asia/Shanghai"; // Java-style tz ids (TWS requirement)

        // connection / output
        public string Output;
        public int MaxPages = 500;
        public int ConnectTimeoutSec = 15;
        public int RequestTimeoutSec = 120;
        public bool UseDelayed; // reqMarketDataType(3): accounts without real-time data need this
        public bool TicksMode;  // fetch historical ticks instead of bars

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Unexpected argument: " + a);

                string key = a.Substring(2);
                string val;
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    val = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }
                else
                {
                    // value-less boolean flags
                    if (key.Equals("ticks", StringComparison.OrdinalIgnoreCase))
                    {
                        vals["ticks"] = "1";
                        continue;
                    }
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("Missing value for --" + key);
                    val = args[++i];
                }
                key = key.Replace('_', '-').ToLowerInvariant();
                vals[key] = val;
            }

            foreach (var kv in vals)
            {
                switch (kv.Key)
                {
                    case "host":            o.Host = kv.Value; break;
                    case "port":            o.Port = ParseInt(kv.Key, kv.Value); break;
                    case "client-id":       o.ClientId = ParseInt(kv.Key, kv.Value); break;
                    case "symbol":          o.Symbol = kv.Value; break;
                    case "conid":           o.ConId = ParseInt(kv.Key, kv.Value); break;
                    case "sectype":         o.SecType = kv.Value; break;
                    case "exchange":        o.Exchange = kv.Value; break;
                    case "currency":        o.Currency = kv.Value; break;
                    case "primary-exch":    o.PrimaryExch = kv.Value; break;
                    case "local-symbol":    o.LocalSymbol = kv.Value; break;
                    case "last-trade-date": o.LastTradeDate = kv.Value; break;
                    case "right":           o.Right = kv.Value; break;
                    case "strike":          o.Strike = double.Parse(kv.Value, CultureInfo.InvariantCulture); break;
                    case "multiplier":      o.Multiplier = kv.Value; break;
                    case "trading-class":   o.TradingClass = kv.Value; break;
                    case "include-expired": o.IncludeExpired = ParseBool(kv.Key, kv.Value); break;
                    case "duration":        o.Duration = kv.Value; break;
                    case "bar-size":        o.BarSize = kv.Value; break;
                    case "what-to-show":    o.WhatToShow = kv.Value; break;
                    case "use-rth":         o.UseRth = ParseInt(kv.Key, kv.Value); break;
                    case "end":             o.End = kv.Value; break;
                    case "start":           o.Start = kv.Value; break;
                    case "tz":              o.Timezone = kv.Value; break;
                    case "output":          o.Output = kv.Value; break;
                    case "max-pages":       o.MaxPages = ParseInt(kv.Key, kv.Value); break;
                    case "connect-timeout": o.ConnectTimeoutSec = ParseInt(kv.Key, kv.Value); break;
                    case "request-timeout": o.RequestTimeoutSec = ParseInt(kv.Key, kv.Value); break;
                    case "delayed":         o.UseDelayed = ParseBool(kv.Key, kv.Value); break;
                    case "ticks":           o.TicksMode = ParseBool(kv.Key, kv.Value); break;
                    default:
                        throw new ArgumentException("Unknown option: --" + kv.Key);
                }
            }

            if (string.IsNullOrWhiteSpace(o.Symbol))
                throw new ArgumentException("Missing required option: --symbol");
            if (o.ConnectTimeoutSec <= 0 || o.RequestTimeoutSec <= 0 || o.MaxPages <= 0)
                throw new ArgumentException("--connect-timeout, --request-timeout and --max-pages must be positive");
            if (string.Equals(o.WhatToShow, "SCHEDULE", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--what-to-show SCHEDULE is not supported (it does not return bars)");

            // --bar-size "ticks" is accepted as an alias for ticks mode
            if (string.Equals(o.BarSize, "ticks", StringComparison.OrdinalIgnoreCase))
                o.TicksMode = true;
            if (o.TicksMode && string.IsNullOrWhiteSpace(o.Start))
                throw new ArgumentException("Ticks mode requires --start (e.g. \"20260612 00:00:00\")");

            return o;
        }

        private static int ParseInt(string key, string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
                throw new ArgumentException("Invalid integer for --" + key + ": " + value);
            return r;
        }

        private static bool ParseBool(string key, string value)
        {
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ArgumentException("Invalid boolean for --" + key + ": " + value);
        }
    }
}
