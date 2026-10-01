using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TwsHistory.Core
{
    /// <summary>Options for the offline BA+TRADES merge (no TWS connection involved).</summary>
    public sealed class MergeOptions
    {
        public string TradesDir;    // NQU6_TICKS_YYYYMMDD_HH.csv
        public string BaDir;        // NQU6_TICKSBA_YYYYMMDD_HH.csv
        public string FixTrDir;     // NQU6_TICKS_YYYYMMDD_HH.part2.NN.KK.csv
        public string FixBaDir;     // NQU6_TICKSBA_YYYYMMDD_HH.part2.NN.KK.csv
        public string OutStreamDir; // event stream output
        public string OutSecondDir; // per-second output (optional)
        public string From;         // yyyyMMdd inclusive (optional)
        public string To;           // yyyyMMdd inclusive (optional)
        public bool WriteSecond = true;

        public static MergeOptions Parse(string[] args)
        {
            var o = new MergeOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
                string key = a.Substring(2);
                string val = null;
                int eq = key.IndexOf('=');
                if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                switch (key.ToLowerInvariant())
                {
                    case "trades-dir":  o.TradesDir = val ?? args[++i]; break;
                    case "ba-dir":      o.BaDir = val ?? args[++i]; break;
                    case "fix-tr":      o.FixTrDir = val ?? args[++i]; break;
                    case "fix-ba":      o.FixBaDir = val ?? args[++i]; break;
                    case "out-stream":  o.OutStreamDir = val ?? args[++i]; break;
                    case "out-second":  o.OutSecondDir = val ?? args[++i]; break;
                    case "from":        o.From = val ?? args[++i]; break;
                    case "to":          o.To = val ?? args[++i]; break;
                    case "no-second":   o.WriteSecond = false; break;
                }
            }
            if (string.IsNullOrWhiteSpace(o.OutStreamDir)) throw new ArgumentException("--out-stream is required");
            return o;
        }
    }

    /// <summary>
    /// Merges per-hour TRADES and BID_ASK tick CSVs (plus the ".part2" gap-fill fragments)
    /// into a single tick event stream, and optionally a per-second snapshot series.
    /// Read-only with respect to the source files; outputs go to separate directories.
    /// </summary>
    public static class MergeRunner
    {
        private const string StreamHeader = "time,type,last_price,last_size,bid_price,bid_size,ask_price,ask_size";
        private const string SecondHeader = "time,last_price,last_size,volume,bid_price,bid_size,ask_price,ask_size";

        // forward-filled state, carried across hours
        private static string _lastPx = "", _lastSz = "", _bidPx = "", _bidSz = "", _askPx = "", _askSz = "";

        public static int Run(MergeOptions o, Action<string> info, Action<string> err)
        {
            info = info ?? (_ => { });
            err = err ?? (_ => { });
            Directory.CreateDirectory(o.OutStreamDir);
            if (o.WriteSecond && !string.IsNullOrWhiteSpace(o.OutSecondDir)) Directory.CreateDirectory(o.OutSecondDir);

            // ---- collect hour keys ----
            var hours = new SortedSet<string>(StringComparer.Ordinal);
            CollectHours(o.TradesDir, "NQU6_TICKS_", hours);
            CollectHours(o.BaDir, "NQU6_TICKSBA_", hours);
            CollectHours(o.FixTrDir, "NQU6_TICKS_", hours);
            CollectHours(o.FixBaDir, "NQU6_TICKSBA_", hours);

            var list = hours.Where(h =>
            {
                string d = h.Substring(0, 8);
                if (!string.IsNullOrWhiteSpace(o.From) && string.CompareOrdinal(d, o.From) < 0) return false;
                if (!string.IsNullOrWhiteSpace(o.To) && string.CompareOrdinal(d, o.To) > 0) return false;
                return true;
            }).ToList();

            info($"Merge: {list.Count} hour(s)  {list.FirstOrDefault()} .. {list.LastOrDefault()}");
            int done = 0, empty = 0; long lines = 0; long secLines = 0;

            foreach (var h in list)
            {
                string date = h.Substring(0, 8), hh = h.Substring(9, 2);

                // sources for this hour: main file first, then fragments (already time-ordered)
                var trSrc = SourcesFor(o.TradesDir, o.FixTrDir, "NQU6_TICKS", date, hh);
                var baSrc = SourcesFor(o.BaDir, o.FixBaDir, "NQU6_TICKSBA", date, hh);
                if (trSrc.Count == 0 && baSrc.Count == 0) { empty++; continue; }

                string streamPath = Path.Combine(o.OutStreamDir, $"NQU6_STREAM_{date}_{hh}.csv");
                string secondPath = (o.WriteSecond && !string.IsNullOrWhiteSpace(o.OutSecondDir))
                    ? Path.Combine(o.OutSecondDir, $"NQU6_SECOND_{date}_{hh}.csv") : null;

                long hourLines = 0, hourSec = 0;
                MergeHour(trSrc, baSrc, streamPath, secondPath, date, hh, ref hourLines, ref hourSec);
                lines += hourLines; secLines += hourSec;
                done++;
                if (done % 50 == 0) info($"  .. {done}/{list.Count} hours, {lines:N0} stream lines, {secLines:N0} second lines");
            }

            info($"Done. hours={done} (empty={empty})  streamLines={lines:N0}  secondLines={secLines:N0}");
            info($"Stream dir: {Path.GetFullPath(o.OutStreamDir)}");
            if (o.WriteSecond && !string.IsNullOrWhiteSpace(o.OutSecondDir))
                info($"Second dir: {Path.GetFullPath(o.OutSecondDir)}");
            return 0;
        }

        private static void CollectHours(string dir, string prefix, SortedSet<string> hours)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, prefix + "*.csv"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var parts = name.Split('_');
                if (parts.Length < 3) continue;
                string d = parts[parts.Length - 2];
                string hpart = parts[parts.Length - 1];
                int dot = hpart.IndexOf('.');
                string hh = dot >= 0 ? hpart.Substring(0, dot) : hpart;
                if (d.Length == 8 && hh.Length == 2) hours.Add(d + " " + hh);
            }
        }

        /// <summary>Main hourly file (if present) followed by any ".part2.NN.KK" fragments, name-sorted.</summary>
        private static List<string> SourcesFor(string mainDir, string fixDir, string prefix, string date, string hh)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(mainDir))
            {
                string main = Path.Combine(mainDir, $"{prefix}_{date}_{hh}.csv");
                if (File.Exists(main)) list.Add(main);
            }
            if (!string.IsNullOrWhiteSpace(fixDir) && Directory.Exists(fixDir))
            {
                var frags = Directory.GetFiles(fixDir, $"{prefix}_{date}_{hh}.part2.*.csv").ToList();
                frags.Sort(StringComparer.Ordinal);
                list.AddRange(frags);
            }
            return list;
        }

        private sealed class RowReader : IDisposable
        {
            private readonly StreamReader _r;
            public string Time;      // 19-char timestamp
            public string Line;      // full raw line
            public bool IsTrade;     // true = TRADES row, false = BID_ASK row
            public bool HasRow;

            public RowReader(string path, bool isTrade)
            {
                _r = new StreamReader(path, Encoding.UTF8, false, 1 << 20);
                IsTrade = isTrade;
                _r.ReadLine(); // header
                Advance();
            }
            public void Advance()
            {
                string l = _r.ReadLine();
                if (l == null || l.Length < 19) { HasRow = false; Time = null; Line = null; return; }
                Line = l; Time = l.Substring(0, 19); HasRow = true;
            }
            public void Dispose() => _r.Dispose();
        }

        private static void MergeHour(List<string> trSrc, List<string> baSrc, string streamPath, string secondPath,
                                      string date, string hh, ref long streamLines, ref long secondLines)
        {
            var readers = new List<RowReader>();
            foreach (var p in trSrc) readers.Add(new RowReader(p, true));
            foreach (var p in baSrc) readers.Add(new RowReader(p, false));

            using var wS = new StreamWriter(streamPath, false, new UTF8Encoding(false), 1 << 20);
            wS.WriteLine(StreamHeader);
            StreamWriter w2 = null;
            if (secondPath != null) { w2 = new StreamWriter(secondPath, false, new UTF8Encoding(false), 1 << 20); w2.WriteLine(SecondHeader); }

            // per-second clock for this hour (state itself is carried across hours in the fields)
            var hourStart = DateTime.ParseExact(date + " " + hh + ":00:00", "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture);
            var hourEnd = hourStart.AddMinutes(59).AddSeconds(59);
            var clock = hourStart;
            string curSec = null;
            long curVol = 0;

            try
            {
                string prevEmitted = null;
                while (true)
                {
                    // pick the earliest pending row (same second: TRADES first)
                    RowReader best = null;
                    foreach (var r in readers)
                    {
                        if (!r.HasRow) continue;
                        if (best == null) { best = r; continue; }
                        int c = string.CompareOrdinal(r.Time, best.Time);
                        if (c < 0 || (c == 0 && r.IsTrade && !best.IsTrade)) best = r;
                    }
                    if (best == null) break;

                    string line = best.Line;
                    string time = best.Time;
                    bool isTrade = best.IsTrade;
                    best.Advance();

                    if (line == prevEmitted) continue;   // adjacent duplicate (fragment boundary)
                    prevEmitted = line;

                    var p = line.Split(',');
                    if (isTrade)
                    {
                        _lastPx = p[1]; _lastSz = p[2];
                        wS.Write(time); wS.Write(",T,"); wS.Write(p[1]); wS.Write(','); wS.Write(p[2]);
                        wS.Write(','); wS.Write(_bidPx); wS.Write(','); wS.Write(_bidSz);
                        wS.Write(','); wS.Write(_askPx); wS.Write(','); wS.WriteLine(_askSz);
                    }
                    else
                    {
                        _bidPx = p[1]; _bidSz = p[2]; _askPx = p[3]; _askSz = p[4];
                        wS.Write(time); wS.Write(",Q,"); wS.Write(_lastPx); wS.Write(','); wS.Write(_lastSz);
                        wS.Write(','); wS.Write(p[1]); wS.Write(','); wS.Write(p[2]);
                        wS.Write(','); wS.Write(p[3]); wS.Write(','); wS.WriteLine(p[4]);
                    }
                    streamLines++;

                    // ---- per-second series ----
                    if (w2 != null)
                    {
                        var secDt = DateTime.ParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        if (curSec == null) curSec = time;
                        else if (!string.Equals(curSec, time, StringComparison.Ordinal))
                        {
                            // clamp to this hour: source files may carry a few rows from the next hour
                            var lim = secDt < hourEnd.AddSeconds(1) ? secDt : hourEnd.AddSeconds(1);
                            FlushSeconds(w2, ref clock, lim, curSec, ref curVol, ref secondLines);
                            curSec = time;
                        }
                        if (isTrade) curVol += long.Parse(p[2], CultureInfo.InvariantCulture);
                    }
                }

                // tail: flush remaining seconds up to (and including) the hour end
                if (w2 != null) FlushSeconds(w2, ref clock, hourEnd.AddSeconds(1), curSec, ref curVol, ref secondLines);
            }
            finally
            {
                foreach (var r in readers) r.Dispose();
                wS.Dispose();
                w2?.Dispose();
            }
        }

        /// <summary>
        /// Emits every second in [clock, limit) using the current state; volume is attributed to the
        /// second whose trades produced it (0 for idle seconds).
        /// </summary>
        private static void FlushSeconds(StreamWriter w, ref DateTime clock, DateTime limit, string volSec,
                                         ref long vol, ref long secondLines)
        {
            while (clock < limit)
            {
                long v = (volSec != null && string.Equals(clock.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), volSec, StringComparison.Ordinal)) ? vol : 0;
                w.Write(clock.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                w.Write(','); w.Write(_lastPx); w.Write(','); w.Write(_lastSz); w.Write(','); w.Write(v);
                w.Write(','); w.Write(_bidPx); w.Write(','); w.Write(_bidSz);
                w.Write(','); w.Write(_askPx); w.Write(','); w.WriteLine(_askSz);
                secondLines++;
                clock = clock.AddSeconds(1);
            }
            vol = 0;
        }
    }
}
