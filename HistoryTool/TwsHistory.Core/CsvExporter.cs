using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TwsHistory.Core
{
    /// <summary>Writes fetched bars to CSV and computes default file names.</summary>
    public static class CsvExporter
    {
        public static string DefaultOutputName(CliOptions opt)
        {
            string bs = Regex.Replace(opt.BarSize, @"\s+", "");
            string sym = string.IsNullOrWhiteSpace(opt.LocalSymbol) ? opt.Symbol : opt.LocalSymbol;
            string range = "";
            try
            {
                string start = string.IsNullOrWhiteSpace(opt.Start) ? "" : TimeUtil.Parse(opt.Start).ToString("yyyyMMdd");
                string end = string.IsNullOrWhiteSpace(opt.End) ? DateTime.Now.ToString("yyyyMMdd") : TimeUtil.Parse(opt.End).ToString("yyyyMMdd");
                if (start.Length > 0) range = $"{start}_{end}";
            }
            catch { /* keep plain name */ }
            return range.Length > 0
                ? $"{sym}_{opt.SecType}_{bs}_{range}_{opt.WhatToShow}.csv"
                : $"{sym}_{opt.SecType}_{bs}_{opt.WhatToShow}.csv";
        }

        public static void Write(string path, List<BarRecord> bars)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("time,open,high,low,close,volume,wap,count");
                foreach (var b in bars)
                {
                    w.Write(b.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    w.Write(',');
                    w.Write(N(b.Open));
                    w.Write(',');
                    w.Write(N(b.High));
                    w.Write(',');
                    w.Write(N(b.Low));
                    w.Write(',');
                    w.Write(N(b.Close));
                    w.Write(',');
                    w.Write(b.Volume.ToString(CultureInfo.InvariantCulture));
                    w.Write(',');
                    w.Write(b.Wap.ToString(CultureInfo.InvariantCulture));
                    w.Write(',');
                    w.WriteLine(b.Count.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        public static string DefaultTicksOutputName(CliOptions opt)
        {
            string sym = string.IsNullOrWhiteSpace(opt.LocalSymbol) ? opt.Symbol : opt.LocalSymbol;
            string start = "";
            string end = "";
            try
            {
                start = string.IsNullOrWhiteSpace(opt.Start) ? "" : TimeUtil.Parse(opt.Start).ToString("yyyyMMdd");
                end = string.IsNullOrWhiteSpace(opt.End) ? DateTime.Now.ToString("yyyyMMdd") : TimeUtil.Parse(opt.End).ToString("yyyyMMdd");
            }
            catch { /* keep plain */ }
            string range = start.Length > 0 ? $"{start}_{end}" : "";
            return range.Length > 0 ? $"{sym}_TICKS_{range}.csv" : $"{sym}_TICKS.csv";
        }

        public static void WriteTicks(string path, List<TickRecord> ticks)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("time,price,size");
                foreach (var t in ticks)
                {
                    w.Write(t.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    w.Write(',');
                    w.Write(N(t.Price));
                    w.Write(',');
                    w.WriteLine(t.Size.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
