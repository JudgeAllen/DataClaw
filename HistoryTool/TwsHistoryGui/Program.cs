using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TwsHistory.Core;

namespace TwsHistoryGui
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool smoke = args.Length > 0 && args[0] == "--smoke";
            var probe = Path.Combine(AppContext.BaseDirectory, "startup.log");
            void Probe(string msg)
            {
                try { File.AppendAllText(probe, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}", new System.Text.UTF8Encoding(true)); } catch { }
            }

            try
            {
                Probe("begin");
                ApplicationConfiguration.Initialize();
                Probe("config ok");

                if (args.Length > 0 && args[0] == "--fetch")
                {
                    // Drive the real GUI flow from the command line: fill the form,
                    // start the fetch, keep the window open.
                    var opt = TwsHistory.Core.CliOptions.Parse(args.Skip(1).ToArray());
                    using (var form = new MainForm())
                    {
                        form.Shown += async (_, _) =>
                        {
                            try
                            {
                                bool ok = await form.FetchWithOptions(opt);
                                Probe($"fetch ok={ok}");
                            }
                            catch (Exception ex)
                            {
                                Probe("fetch exception: " + ex);
                            }
                        };
                        Application.Run(form);
                    }
                    Probe("fetch mode done");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--selftest")
                {
                    // Real end-to-end test: fetch one day of AAPL daily bars,
                    // refresh the data list, load a preview, then exit.
                    string dataDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "twshistory_selftest");
                    using (var form = new MainForm())
                    {
                        form.SelfTestConfigure(dataDir);
                        form.Shown += async (_, _) =>
                        {
                            bool ok = await form.SelfTestRunAsync();
                            Probe($"selftest ok={ok}");
                            form.Close();
                        };
                        Application.Run(form);
                    }
                    Probe("selftest done");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--dump-ui")
                {
                    // Layout check: print the control tree so it can be reviewed.
                    using var form = new MainForm();
                    form.Shown += (_, _) =>
                    {
                        DumpControls(form, 0, Probe);
                        form.Close();
                    };
                    Application.Run(form);
                    Probe("dump-ui done");
                    return 0;
                }

                if (smoke)
                {
                    // Automated smoke test: build the form, run message loop for 2s, exit.
                    using var form = new MainForm();
                    form.Shown += (_, _) =>
                    {
                        Probe("shown");
                        var t = new Timer { Interval = 2000 };
                        t.Tick += (_, _) => { t.Stop(); Probe("closing"); form.Close(); };
                        t.Start();
                    };
                    Application.Run(form);
                    Probe("run returned");
                    return 0;
                }

                using (var form = new MainForm())
                {
                    Probe("form built");
                    Application.Run(form);
                    Probe("run returned");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Probe("EXCEPTION: " + ex);
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString()); } catch { }
                return 1;
            }
        }
        private static void DumpControls(Control c, int depth, Action<string> probe)
        {
            try
            {
                probe(new string(' ', depth * 2) + $"{c.GetType().Name} '{c.Text}' at ({c.Left},{c.Top}) size ({c.Width}x{c.Height}) visible={c.Visible} enabled={c.Enabled}");
                foreach (Control child in c.Controls)
                    DumpControls(child, depth + 1, probe);
            }
            catch { }
        }
    }
}
