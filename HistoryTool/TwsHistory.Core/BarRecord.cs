using System;
using System.Globalization;
using System.Text.RegularExpressions;
using IBApi;

namespace TwsHistory.Core
{
    /// <summary>Parses the "yyyyMMdd [HH:mm:ss]" time strings TWS returns.</summary>
    public static class TimeUtil
    {
        private static readonly string[] Formats = { "yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm", "yyyyMMdd" };

        public static DateTime Parse(string s)
        {
            s = s.Trim();
            foreach (var f in Formats)
                if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return d;

            // Newer TWS versions append the timezone to time strings,
            // e.g. "20260814 22:19:05 Asia/Shanghai" or "... US/Eastern".
            var m = Regex.Match(s, @"^(\d{8}(?: \d{2}:\d{2}(?::\d{2})?)?)\s+[A-Za-z].*$");
            if (m.Success)
            {
                foreach (var f in Formats)
                    if (DateTime.TryParseExact(m.Groups[1].Value, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
                        return d2;
            }

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var g))
                return g;
            throw new FormatException("Cannot parse time: '" + s + "'");
        }
    }

    /// <summary>A single OHLC bar with a parsed timestamp.</summary>
    public sealed class BarRecord
    {
        public DateTime Time;
        public double Open, High, Low, Close;
        public decimal Volume, Wap;
        public int Count;

        /// <summary>Returns null for the -1/-1/-1/-1 "empty bar" sentinel TWS sends.</summary>
        public static BarRecord FromBar(Bar bar)
        {
            if (bar.Open < 0 && bar.High < 0 && bar.Low < 0 && bar.Close < 0)
                return null;

            DateTime t;
            try { t = TimeUtil.Parse(bar.Time); }
            catch (FormatException) { return null; }

            return new BarRecord
            {
                Time = t,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                Wap = bar.WAP,
                Count = bar.Count
            };
        }
    }

    /// <summary>One page of historical data as returned by a single request.</summary>
    public sealed class PageResult
    {
        public System.Collections.Generic.List<BarRecord> Bars { get; } = new System.Collections.Generic.List<BarRecord>();
        public DateTime? EarliestTime;
        public string EarliestKey;
        public string EarliestTimeText;
        public string Error;
        public bool Finished;
        public bool Cancelled;
        public string ServerStart;
        public string ServerEnd;
        public int DroppedBars; // bars skipped (empty sentinel or unparseable time)

        public void Finish(string start = null, string end = null)
        {
            ServerStart = start;
            ServerEnd = end;
            if (Bars.Count > 0)
            {
                DateTime min = Bars[0].Time;
                for (int i = 1; i < Bars.Count; i++)
                    if (Bars[i].Time < min) min = Bars[i].Time;
                EarliestTime = min;
                EarliestKey = min.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture);
                EarliestTimeText = EarliestKey;
            }
            Finished = true;
        }
    }
}
