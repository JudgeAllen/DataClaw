using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TwsHistoryGui
{
    /// <summary>One row of a downloaded CSV file.</summary>
    public sealed class CsvRow
    {
        public DateTime Time;
        public double Open, High, Low, Close;
        public decimal Volume, Wap;
        public int Count;
    }

    /// <summary>Summary of a CSV file, for the file list.</summary>
    public sealed class CsvInfo
    {
        public string Path;
        public string Name;
        public long SizeBytes;
        public DateTime Modified;
        public int Rows;
        public DateTime? FirstTime;
        public DateTime? LastTime;
        public bool ReadFailed;
    }

    /// <summary>Reads the CSV files produced by CsvExporter (simple comma format).</summary>
    public static class CsvFile
    {
        public static CsvInfo Inspect(string path)
        {
            var fi = new FileInfo(path);
            var info = new CsvInfo { Path = path, Name = fi.Name, SizeBytes = fi.Length, Modified = fi.LastWriteTime };
            try
            {
                bool first = true;
                foreach (var line in File.ReadLines(path))
                {
                    if (first) { first = false; continue; } // header
                    if (line.Length == 0) continue;
                    info.Rows++;
                    var t = ParseTime(line);
                    if (info.FirstTime == null) info.FirstTime = t;
                    info.LastTime = t;
                }
            }
            catch { info.ReadFailed = true; }
            return info;
        }

        /// <summary>Reads up to maxRows data rows (header skipped). Throws on parse errors.</summary>
        public static List<CsvRow> ReadRows(string path, int maxRows = 5000)
        {
            var rows = new List<CsvRow>();
            bool first = true;
            foreach (var line in File.ReadLines(path))
            {
                if (first) { first = false; continue; }
                if (line.Length == 0) continue;
                var r = ParseRow(line);
                if (r != null)
                {
                    rows.Add(r);
                    if (rows.Count >= maxRows) break;
                }
            }
            return rows;
        }

        private static DateTime ParseTime(string line)
        {
            string col = line.Split(',')[0].Trim();
            if (DateTime.TryParseExact(col, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t1)) return t1;
            if (DateTime.TryParseExact(col, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2)) return t2;
            return DateTime.MinValue;
        }

        private static CsvRow ParseRow(string line)
        {
            var p = line.Split(',');
            if (p.Length < 5) return null;
            if (!DateTime.TryParseExact(p[0].Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                if (!DateTime.TryParseExact(p[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                    return null;
            }
            if (!TryD(p[1], out var o) || !TryD(p[2], out var h) || !TryD(p[3], out var l) || !TryD(p[4], out var c))
                return null;

            var row = new CsvRow { Time = time, Open = o, High = h, Low = l, Close = c };
            if (p.Length > 5) decimal.TryParse(p[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out row.Volume);
            if (p.Length > 6) decimal.TryParse(p[6].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out row.Wap);
            if (p.Length > 7) int.TryParse(p[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out row.Count);
            return row;
        }

        private static bool TryD(string s, out double v)
        {
            return double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
        }
    }
}
