using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IBApi;
using TwsHistory.Core;

namespace TwsHistoryGui
{
    public sealed class MainForm : Form
    {
        // ------------------------------------------------------------ fetch tab
        private readonly ComboBox presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox symbolBox = new TextBox();
        private readonly ComboBox sectypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox exchangeBox = new TextBox();
        private readonly TextBox currencyBox = new TextBox();
        private readonly TextBox primaryExchBox = new TextBox();
        private readonly TextBox localSymbolBox = new TextBox();
        private readonly TextBox lastTradeDateBox = new TextBox();
        private readonly ComboBox rightCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox strikeBox = new TextBox();
        private readonly TextBox multiplierBox = new TextBox();
        private readonly TextBox tradingClassBox = new TextBox();
        private readonly CheckBox includeExpiredBox = new CheckBox { Text = "允许已过期合约(IncludeExpired)" };
        private readonly TextBox durationBox = new TextBox { Text = "1 D" };
        private readonly ComboBox barSizeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox whatToShowCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox useRthBox = new CheckBox { Text = "仅常规交易时段 (RTH)" };
        private readonly CheckBox endEnableBox = new CheckBox { Text = "指定结束时间(不勾选 = 现在)", AutoSize = true };
        private readonly DateTimePicker endPicker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 175 };
        private readonly CheckBox startEnableBox = new CheckBox { Text = "指定开始时间(不勾选 = 自动)", AutoSize = true };
        private readonly DateTimePicker startPicker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 175 };
        private readonly ComboBox tzCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox hostBox = new TextBox();
        private readonly TextBox portBox = new TextBox();
        private readonly TextBox clientIdBox = new TextBox();
        private readonly TextBox outputBox = new TextBox();
        private readonly Button browseOutputBtn = new Button { Text = "浏览…" };
        private readonly Button testConnBtn = new Button { Text = "测试连接" };
        private readonly Button startBtn = new Button { Text = "开始拉取" };
        private readonly Button cancelBtn = new Button { Text = "中止" };
        private readonly Button clearLogBtn = new Button { Text = "清空日志" };
        private readonly TextBox logBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Font = new Font("Consolas", 9f), Dock = DockStyle.Fill };

        // ------------------------------------------------------------ data tab
        private readonly TextBox dataDirBox = new TextBox();
        private readonly Button dataBrowseBtn = new Button { Text = "浏览…" };
        private readonly Button refreshBtn = new Button { Text = "刷新" };
        private readonly Label dataStatusLabel = new Label { AutoSize = true };
        private readonly ListView listView = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        private readonly DataGridView grid = new DataGridView { ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AllowUserToResizeRows = false };
        private readonly PictureBox chartBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.White, SizeMode = PictureBoxSizeMode.Normal };
        private readonly Label previewInfoLabel = new Label { AutoSize = true };

        private readonly AppSettings settings = AppSettings.Load();
        private CancellationTokenSource cts;
        private bool fetching;
        private List<CsvRow> chartRows;

        public MainForm()
        {
            Text = "TwsHistory — TWS 历史数据工具";
            Width = 1000;
            Height = 780;
            MinimumSize = new Size(900, 660);
            StartPosition = FormStartPosition.CenterScreen;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildFetchTab());
            tabs.TabPages.Add(BuildDataTab());
            Controls.Add(tabs);

            InitFetchTabDefaults();
            LoadSettingsToUi();
            UpdateOutputName();

            FormClosing += (_, e) =>
            {
                if (fetching)
                {
                    cts?.Cancel();
                    // give the background fetch a moment to wind down
                    for (int i = 0; i < 30 && fetching; i++)
                    {
                        Application.DoEvents();
                        Thread.Sleep(100);
                    }
                }
                settings.Save();
            };
        }

        // ================================================================ fetch tab

        private TabPage BuildFetchTab()
        {
            var page = new TabPage("拉取数据");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 510));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            left.Controls.Add(BuildConnGroup(), 0, 0);
            left.Controls.Add(BuildContractGroup(), 0, 1);
            left.Controls.Add(BuildDataGroup(), 0, 2);
            left.Controls.Add(BuildOutputGroup(), 0, 3);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            startBtn.Size = new Size(110, 32);
            cancelBtn.Size = new Size(90, 32);
            cancelBtn.Enabled = false;
            btnRow.Controls.Add(startBtn);
            btnRow.Controls.Add(cancelBtn);
            left.Controls.Add(btnRow, 0, 4);
            for (int i = 0; i < 5; i++) left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var logGroup = new GroupBox { Text = "日志", Dock = DockStyle.Fill };
            var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logLayout.Controls.Add(logBox, 0, 0);
            clearLogBtn.Dock = DockStyle.Right;
            logLayout.Controls.Add(clearLogBtn, 0, 1);
            logGroup.Controls.Add(logLayout);

            layout.Controls.Add(left, 0, 0);
            layout.Controls.Add(logGroup, 1, 0);
            page.Controls.Add(layout);

            startBtn.Click += async (_, _) => await StartFetchAsync();
            cancelBtn.Click += (_, _) => cts?.Cancel();
            clearLogBtn.Click += (_, _) => logBox.Clear();
            browseOutputBtn.Click += (_, _) =>
            {
                using var dlg = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = Path.GetFileName(outputBox.Text), InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(outputBox.Text)) };
                if (dlg.ShowDialog(this) == DialogResult.OK) outputBox.Text = dlg.FileName;
            };
            testConnBtn.Click += async (_, _) => await TestConnectionAsync();

            return page;
        }

        private GroupBox BuildConnGroup()
        {
            var g = new GroupBox { Text = "连接", Dock = DockStyle.Fill, AutoSize = true };
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 7, Padding = new Padding(4), MaximumSize = new Size(470, 0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            hostBox.Dock = DockStyle.Fill;
            portBox.Dock = DockStyle.Fill;
            clientIdBox.Dock = DockStyle.Fill;
            p.Controls.Add(new Label { Text = "主机", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            p.Controls.Add(hostBox, 1, 0);
            p.Controls.Add(new Label { Text = "端口", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
            p.Controls.Add(portBox, 3, 0);
            p.Controls.Add(new Label { Text = "clientId", AutoSize = true, Anchor = AnchorStyles.Left }, 4, 0);
            p.Controls.Add(clientIdBox, 5, 0);
            testConnBtn.Margin = new Padding(8, 0, 0, 0);
            p.Controls.Add(testConnBtn, 6, 0);
            g.Controls.Add(p);
            return g;
        }

        private GroupBox BuildContractGroup()
        {
            var g = new GroupBox { Text = "合约", Dock = DockStyle.Fill, AutoSize = true };
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(4), MaximumSize = new Size(470, 0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            int row = 0;
            AddPair(p, "品种预设", presetCombo, "", null, ref row);
            AddPair(p, "代码 (Symbol) *", symbolBox, "证券类型", sectypeCombo, ref row);
            AddPair(p, "交易所", exchangeBox, "币种", currencyBox, ref row);
            AddPair(p, "主交易所", primaryExchBox, "本地代码", localSymbolBox, ref row);
            AddPair(p, "到期日 (YYYYMMDD)", lastTradeDateBox, "认购/认沽", rightCombo, ref row);
            AddPair(p, "行权价", strikeBox, "乘数", multiplierBox, ref row);
            AddPair(p, "交易类别", tradingClassBox, "", null, ref row);
            p.Controls.Add(includeExpiredBox, 1, row);
            p.SetColumnSpan(includeExpiredBox, 3);
            p.RowCount = row + 1;

            presetCombo.Items.AddRange(new object[] { "股票 STK", "期权 OPT", "期货 FUT", "外汇 CASH", "指数 IND" });
            sectypeCombo.Items.AddRange(new object[] { "STK", "OPT", "FUT", "FOP", "CASH", "IND", "BAG", "WAR", "BOND", "CMDTY", "FUND" });
            rightCombo.Items.AddRange(new object[] { "", "C", "P" });
            sectypeCombo.SelectedIndex = 0;
            rightCombo.SelectedIndex = 0;

            presetCombo.SelectedIndexChanged += (_, _) =>
            {
                if (presetCombo.SelectedItem == null) return;
                var preset = presetCombo.SelectedItem.ToString();
                if (preset.StartsWith("股票")) { sectypeCombo.SelectedItem = "STK"; exchangeBox.Text = "SMART"; currencyBox.Text = "USD"; }
                else if (preset.StartsWith("期权")) { sectypeCombo.SelectedItem = "OPT"; exchangeBox.Text = "SMART"; currencyBox.Text = "USD"; }
                else if (preset.StartsWith("期货")) { sectypeCombo.SelectedItem = "FUT"; exchangeBox.Text = "SMART"; currencyBox.Text = "USD"; }
                else if (preset.StartsWith("外汇")) { sectypeCombo.SelectedItem = "CASH"; exchangeBox.Text = "IDEALPRO"; currencyBox.Text = "USD"; }
                else if (preset.StartsWith("指数")) { sectypeCombo.SelectedItem = "IND"; exchangeBox.Text = "SMART"; currencyBox.Text = "USD"; }
                UpdateOutputName();
            };
            HookOutputNameChange(symbolBox, sectypeCombo, barSizeCombo, whatToShowCombo);

            g.Controls.Add(p);
            return g;
        }

        private GroupBox BuildDataGroup()
        {
            var g = new GroupBox { Text = "历史数据", Dock = DockStyle.Fill, AutoSize = true };
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(4), MaximumSize = new Size(470, 0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            barSizeCombo.Items.AddRange(new object[] { "1 secs", "5 secs", "10 secs", "15 secs", "30 secs", "1 min", "2 mins", "3 mins", "5 mins", "10 mins", "15 mins", "30 mins", "1 hour", "2 hours", "4 hours", "1 day", "1 week", "1 month" });
            barSizeCombo.SelectedItem = "1 day";
            whatToShowCombo.Items.AddRange(new object[] { "TRADES", "MIDPOINT", "BID", "ASK", "BID_ASK", "HISTORICAL_VOLATILITY", "OPTION_IMPLIED_VOLATILITY", "FEE_RATE" });
            whatToShowCombo.SelectedItem = "TRADES";

            var endFlow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            endFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            endFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            endPicker.Margin = new Padding(6, 0, 0, 0);
            endFlow.Controls.Add(endEnableBox, 0, 0);
            endFlow.Controls.Add(endPicker, 1, 0);
            var startFlow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            startFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            startFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            startPicker.Margin = new Padding(6, 0, 0, 0);
            startFlow.Controls.Add(startEnableBox, 0, 0);
            startFlow.Controls.Add(startPicker, 1, 0);

            endEnableBox.CheckedChanged += (_, _) =>
            {
                endPicker.Enabled = endEnableBox.Checked;
                UpdateOutputName();
            };
            startEnableBox.CheckedChanged += (_, _) =>
            {
                startPicker.Enabled = startEnableBox.Checked;
                UpdateOutputName();
            };
            endPicker.ValueChanged += (_, _) => UpdateOutputName();
            startPicker.ValueChanged += (_, _) => UpdateOutputName();
            endPicker.Enabled = false;
            startPicker.Enabled = false;

            int row = 0;

            p.Controls.Add(new Label { Text = "时长 (Duration)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            durationBox.Dock = DockStyle.Fill;
            p.Controls.Add(durationBox, 1, row);
            p.Controls.Add(new Label { Text = "K线大小", AutoSize = true, Anchor = AnchorStyles.Left }, 2, row);
            barSizeCombo.Dock = DockStyle.Fill;
            p.Controls.Add(barSizeCombo, 3, row);
            row++;

            p.Controls.Add(new Label { Text = "数据类型", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            whatToShowCombo.Dock = DockStyle.Fill;
            p.Controls.Add(whatToShowCombo, 1, row);
            p.SetColumnSpan(whatToShowCombo, 3);
            row++;

            p.Controls.Add(useRthBox, 1, row);
            p.SetColumnSpan(useRthBox, 3);
            row++;

            p.Controls.Add(new Label { Text = "结束时间", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            p.Controls.Add(endFlow, 1, row);
            p.SetColumnSpan(endFlow, 3);
            row++;

            p.Controls.Add(new Label { Text = "开始时间", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            p.Controls.Add(startFlow, 1, row);
            p.SetColumnSpan(startFlow, 3);
            row++;

            p.Controls.Add(new Label { Text = "时区 (tz)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            tzCombo.Dock = DockStyle.Fill;
            tzCombo.Items.AddRange(TimeZones.All);
            p.Controls.Add(tzCombo, 1, row);
            p.SetColumnSpan(tzCombo, 3);
            row++;

            p.RowCount = row;
            g.Controls.Add(p);
            return g;
        }

        private GroupBox BuildOutputGroup()
        {
            var g = new GroupBox { Text = "输出", Dock = DockStyle.Fill, AutoSize = true };
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(4), MaximumSize = new Size(470, 0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outputBox.Dock = DockStyle.Fill;
            p.Controls.Add(outputBox, 0, 0);
            p.Controls.Add(browseOutputBtn, 1, 0);
            g.Controls.Add(p);
            return g;
        }

        private static void AddPair(TableLayoutPanel p, string label1, Control c1, string label2, Control c2, ref int row, bool spanLabel = false)
        {
            var l1 = new Label { Text = label1, AutoSize = true, Anchor = AnchorStyles.Left };
            p.Controls.Add(l1, 0, row);
            c1.Dock = DockStyle.Fill;
            p.Controls.Add(c1, 1, row);
            if (c2 != null && !string.IsNullOrEmpty(label2))
            {
                p.Controls.Add(new Label { Text = label2, AutoSize = true, Anchor = AnchorStyles.Left }, 2, row);
                c2.Dock = DockStyle.Fill;
                p.Controls.Add(c2, 3, row);
            }
            row++;
        }

        private void HookOutputNameChange(params Control[] controls)
        {
            foreach (var c in controls)
            {
                if (c is TextBox tb) tb.TextChanged += (_, _) => UpdateOutputName();
                else if (c is ComboBox cb) cb.SelectedIndexChanged += (_, _) => UpdateOutputName();
            }
        }

        private void InitFetchTabDefaults()
        {
            hostBox.Text = settings.Host;
            portBox.Text = settings.Port.ToString();
            clientIdBox.Text = settings.ClientId.ToString();
            tzCombo.SelectedItem = settings.Timezone;
            if (tzCombo.SelectedIndex < 0) tzCombo.SelectedItem = "Asia/Shanghai";
            exchangeBox.Text = "SMART";
            currencyBox.Text = "USD";
            presetCombo.SelectedIndex = 0; // 股票
            endPicker.Value = DateTime.Now;
            startPicker.Value = DateTime.Today;
        }

        private void LoadSettingsToUi()
        {
            dataDirBox.Text = settings.DataDir;
        }

        private void UpdateOutputName()
        {
            if (fetching) return;
            try
            {
                var name = CsvExporter.DefaultOutputName(new CliOptions
                {
                    Symbol = symbolBox.Text.Trim(),
                    SecType = sectypeCombo.SelectedItem?.ToString() ?? "STK",
                    BarSize = barSizeCombo.SelectedItem?.ToString() ?? "1 day",
                    WhatToShow = whatToShowCombo.SelectedItem?.ToString() ?? "TRADES",
                    LocalSymbol = localSymbolBox.Text.Trim(),
                    Start = startEnableBox.Checked ? startPicker.Value.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                    End = endEnableBox.Checked ? endPicker.Value.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) : ""
                });
                outputBox.Text = Path.Combine(GetDataDir(), name);
            }
            catch { /* ignore */ }
        }

        private string GetDataDir()
        {
            var dir = dataDirBox.Text.Trim();
            if (dir.Length == 0) dir = settings.DataDir;
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private CliOptions BuildOptions()
        {
            var opt = new CliOptions
            {
                Host = hostBox.Text.Trim(),
                Port = ParseInt(portBox.Text, "端口"),
                ClientId = ParseInt(clientIdBox.Text, "clientId"),
                Symbol = symbolBox.Text.Trim(),
                SecType = sectypeCombo.SelectedItem?.ToString() ?? "STK",
                Exchange = exchangeBox.Text.Trim(),
                Currency = currencyBox.Text.Trim(),
                PrimaryExch = primaryExchBox.Text.Trim(),
                LocalSymbol = localSymbolBox.Text.Trim(),
                LastTradeDate = lastTradeDateBox.Text.Trim(),
                Right = rightCombo.SelectedItem?.ToString() ?? "",
                Multiplier = multiplierBox.Text.Trim(),
                TradingClass = tradingClassBox.Text.Trim(),
                IncludeExpired = includeExpiredBox.Checked,
                Duration = durationBox.Text.Trim(),
                BarSize = barSizeCombo.SelectedItem?.ToString() ?? "1 day",
                WhatToShow = whatToShowCombo.SelectedItem?.ToString() ?? "TRADES",
                UseRth = useRthBox.Checked ? 1 : 0,
                End = endEnableBox.Checked ? endPicker.Value.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                Start = startEnableBox.Checked ? startPicker.Value.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                Timezone = tzCombo.SelectedItem?.ToString() ?? "Asia/Shanghai",
                Output = outputBox.Text.Trim()
            };
            if (!string.IsNullOrWhiteSpace(strikeBox.Text))
                opt.Strike = double.Parse(strikeBox.Text.Trim(), CultureInfo.InvariantCulture);
            if (opt.Symbol.Length == 0) throw new InvalidOperationException("请填写合约代码 (Symbol)。");
            if (opt.Host.Length == 0) throw new InvalidOperationException("请填写主机地址。");
            if (opt.Timezone.Length == 0) opt.Timezone = "US/Eastern";
            if (opt.Duration.Length == 0) throw new InvalidOperationException("请填写时长 (Duration)。");
            if (opt.Output.Length == 0) throw new InvalidOperationException("请填写输出文件路径。");
            return opt;
        }

        private static int ParseInt(string s, string what)
        {
            if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                throw new InvalidOperationException($"“{what}”必须是整数: “{s}”");
            return v;
        }

        private async Task<bool> StartFetchAsync()
        {
            CliOptions opt;
            try { opt = BuildOptions(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }

            return await ExecuteFetchAsync(opt, null);
        }

        /// <summary>Runs the fetch for the given options; extra sink mirrors log lines to a file (used by --fetch).</summary>
        private async Task<bool> ExecuteFetchAsync(CliOptions opt, Action<string> extra)
        {
            // --fetch mode passes parsed CLI options whose Output is empty; the
            // form's output box (auto-named from the current parameters) is the source of truth.
            if (string.IsNullOrWhiteSpace(opt.Output))
                opt.Output = outputBox.Text.Trim();

            void Sink(string m)
            {
                Log(m);
                try { extra?.Invoke(m); } catch { /* ignore */ }
            }

            fetching = true;
            startBtn.Enabled = false;
            cancelBtn.Enabled = true;
            testConnBtn.Enabled = false;
            cts = new CancellationTokenSource();
            Sink($"开始拉取 {opt.Symbol} {opt.BarSize} ({opt.Duration}) …");

            var fetcher = new HistoryFetcher(opt, Sink, msg => Sink("[TWS] " + msg));
            FetchResult result = null;
            try
            {
                result = await Task.Run(() => fetcher.Fetch(cts.Token));
            }
            catch (Exception ex)
            {
                Sink("异常: " + ex);
            }

            fetching = false;
            startBtn.Enabled = true;
            cancelBtn.Enabled = false;
            testConnBtn.Enabled = true;

            if (result == null) return false;
            if (result.Cancelled) { Sink("已中止。"); return false; }
            if (result.Error != null) { Sink("拉取失败: " + result.Error); return false; }
            if (result.Bars.Count == 0) { Sink("没有返回任何 K 线(检查账户的数据订阅权限)。"); return false; }

            try
            {
                CsvExporter.Write(opt.Output, result.Bars);
                Sink($"完成: {result.Bars.Count:N0} 根 K 线,{result.Pages} 页 → {opt.Output}");
                Sink($"区间: {result.Bars[0].Time:yyyy-MM-dd HH:mm:ss}  ..  {result.Bars[result.Bars.Count - 1].Time:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                Sink("写入 CSV 失败: " + ex.Message);
                return false;
            }

            var outDir = Path.GetDirectoryName(Path.GetFullPath(opt.Output));
            if (string.Equals(outDir, GetDataDir(), StringComparison.OrdinalIgnoreCase))
                RefreshDataList(selectName: Path.GetFileName(opt.Output));
            return true;
        }

        // ------------------------------------------------------------ self test

        /// <summary>Fills the form from parsed CLI options (used by --fetch).</summary>
        public void ApplyOptionsToForm(CliOptions opt)
        {
            hostBox.Text = opt.Host;
            portBox.Text = opt.Port.ToString();
            clientIdBox.Text = opt.ClientId.ToString();
            symbolBox.Text = opt.Symbol;
            sectypeCombo.SelectedItem = opt.SecType;
            if (sectypeCombo.SelectedIndex < 0 && sectypeCombo.Items.Count > 0) sectypeCombo.SelectedIndex = 0;
            exchangeBox.Text = opt.Exchange;
            currencyBox.Text = opt.Currency;
            primaryExchBox.Text = opt.PrimaryExch;
            localSymbolBox.Text = opt.LocalSymbol;
            lastTradeDateBox.Text = opt.LastTradeDate;
            rightCombo.SelectedItem = opt.Right;
            strikeBox.Text = opt.Strike > 0 ? opt.Strike.ToString(CultureInfo.InvariantCulture) : "";
            multiplierBox.Text = opt.Multiplier;
            tradingClassBox.Text = opt.TradingClass;
            includeExpiredBox.Checked = opt.IncludeExpired;
            durationBox.Text = opt.Duration;
            barSizeCombo.SelectedItem = opt.BarSize;
            if (barSizeCombo.SelectedIndex < 0 && barSizeCombo.Items.Count > 0) barSizeCombo.SelectedIndex = 0;
            whatToShowCombo.SelectedItem = opt.WhatToShow;
            useRthBox.Checked = opt.UseRth != 0;

            endEnableBox.Checked = !string.IsNullOrWhiteSpace(opt.End);
            if (endEnableBox.Checked && DateTime.TryParseExact(opt.End.Trim(), "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endT))
                endPicker.Value = endT;
            startEnableBox.Checked = !string.IsNullOrWhiteSpace(opt.Start);
            if (startEnableBox.Checked && DateTime.TryParseExact(opt.Start.Trim(), "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startT))
                startPicker.Value = startT;

            tzCombo.SelectedItem = opt.Timezone;
            if (tzCombo.SelectedIndex < 0) tzCombo.SelectedItem = "Asia/Shanghai";

            UpdateOutputName();
            if (!string.IsNullOrWhiteSpace(opt.Output))
                outputBox.Text = opt.Output;
        }

        /// <summary>Applies options, runs the fetch through the normal GUI flow, keeps the window open.</summary>
        public async Task<bool> FetchWithOptions(CliOptions opt)
        {
            ApplyOptionsToForm(opt);
            Log("--- 命令行参数拉取模式 ---");
            string logFile = Path.Combine(AppContext.BaseDirectory, "fetch.log");
            return await ExecuteFetchAsync(opt, msg =>
            {
                try { File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"); }
                catch { /* ignore */ }
            });
        }

        /// <summary>Fills the form with test values (used by --selftest).</summary>
        public void SelfTestConfigure(string dataDir)
        {
            symbolBox.Text = "AAPL";
            durationBox.Text = "1 D";
            barSizeCombo.SelectedItem = "5 mins"; // exercises the intraday path
            whatToShowCombo.SelectedItem = "TRADES";
            clientIdBox.Text = "99";
            dataDirBox.Text = dataDir;
            Directory.CreateDirectory(dataDir);
            // exercise the checked-picker path: fixed end time and pagination start
            endEnableBox.Checked = true;
            endPicker.Value = new DateTime(2026, 8, 13, 16, 0, 0);
            startEnableBox.Checked = true;
            startPicker.Value = new DateTime(2026, 8, 1, 0, 0, 0);
            UpdateOutputName();
        }

        /// <summary>Runs a real fetch and refreshes the data list (used by --selftest).</summary>
        public async Task<bool> SelfTestRunAsync()
        {
            try
            {
                bool ok = await StartFetchAsync();
                if (ok)
                {
                    RefreshDataList();
                    LoadPreview();
                }
                return ok;
            }
            catch (Exception ex)
            {
                Log("selftest error: " + ex.Message);
                return false;
            }
        }

        private async Task TestConnectionAsync()
        {
            int port, cid;
            try { port = ParseInt(portBox.Text, "端口"); cid = ParseInt(clientIdBox.Text, "clientId"); }
            catch (Exception ex) { Log(ex.Message); return; }

            testConnBtn.Enabled = false;
            Log($"测试连接 {hostBox.Text.Trim()}:{port} (clientId {cid}) …");
            await Task.Run(() =>
            {
                var wrapper = new TwsWrapper(msg => SafeLog("[TWS] " + msg));
                var signal = new EReaderMonitorSignal();
                var client = new EClientSocket(wrapper, signal);
                try
                {
                    client.eConnect(hostBox.Text.Trim(), port, cid);
                    var reader = new EReader(client, signal);
                    reader.Start();
                    var thread = new Thread(() =>
                    {
                        while (client.IsConnected()) { signal.waitForSignal(); reader.processMsgs(); }
                    }) { IsBackground = true };
                    thread.Start();

                    if (wrapper.WaitUntilConnected(TimeSpan.FromSeconds(10)))
                        SafeLog($"连接成功: server version {client.ServerVersion}, 服务器时间 {client.ServerTime}");
                    else
                        SafeLog("连接失败: " + wrapper.LastErrorText);
                }
                catch (Exception ex)
                {
                    SafeLog("连接异常: " + ex.Message);
                }
                finally
                {
                    try { client.eDisconnect(); } catch { }
                }
            });
            testConnBtn.Enabled = true;
        }

        // ================================================================ data tab

        private TabPage BuildDataTab()
        {
            var page = new TabPage("数据管理");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // row 0: data dir
            var dirRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.Controls.Add(new Label { Text = "数据目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            dataDirBox.Dock = DockStyle.Fill;
            dirRow.Controls.Add(dataDirBox, 1, 0);
            dirRow.Controls.Add(dataBrowseBtn, 2, 0);
            dirRow.Controls.Add(refreshBtn, 3, 0);
            layout.Controls.Add(dirRow, 0, 0);

            // row 1: actions
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            var previewBtn = new Button { Text = "预览" };
            var openBtn = new Button { Text = "打开" };
            var delBtn = new Button { Text = "删除" };
            var renameBtn = new Button { Text = "重命名" };
            var folderBtn = new Button { Text = "打开所在文件夹" };
            actions.Controls.AddRange(new Control[] { previewBtn, openBtn, delBtn, renameBtn, folderBtn, dataStatusLabel });
            layout.Controls.Add(actions, 0, 1);

            // row 2: list
            listView.Dock = DockStyle.Fill;
            listView.Columns.Add("文件名", 240);
            listView.Columns.Add("大小", 80);
            listView.Columns.Add("K线数", 90);
            listView.Columns.Add("区间", 230);
            listView.Columns.Add("修改时间", 130);
            layout.Controls.Add(listView, 0, 2);
            layout.RowStyles[2] = new RowStyle(SizeType.Absolute, 170);

            // row 3: preview split (grid + chart)
            var previewPanel = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 430, Orientation = Orientation.Vertical };
            grid.Dock = DockStyle.Fill;
            previewPanel.Panel1.Controls.Add(grid);
            chartBox.Dock = DockStyle.Fill;
            previewPanel.Panel2.Controls.Add(chartBox);
            chartBox.Paint += ChartBox_Paint;
            layout.Controls.Add(previewPanel, 0, 3);

            dataBrowseBtn.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = GetDataDir() };
                if (dlg.ShowDialog(this) == DialogResult.OK) dataDirBox.Text = dlg.SelectedPath;
            };
            refreshBtn.Click += (_, _) => RefreshDataList();
            dataDirBox.TextChanged += (_, _) => { settings.DataDir = dataDirBox.Text.Trim(); UpdateOutputName(); };

            listView.SelectedIndexChanged += (_, _) => LoadPreview();
            previewBtn.Click += (_, _) => LoadPreview(force: true);
            openBtn.Click += (_, _) => OpenSelected();
            delBtn.Click += (_, _) => DeleteSelected();
            renameBtn.Click += (_, _) => RenameSelected();
            folderBtn.Click += (_, _) => OpenFolderOfSelected();

            page.Controls.Add(layout);
            return page;
        }

        private void RefreshDataList(string selectName = null)
        {
            listView.BeginUpdate();
            listView.Items.Clear();
            int total = 0;
            try
            {
                var dir = GetDataDir();
                var files = Directory.GetFiles(dir, "*.csv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var f in files)
                {
                    var info = CsvFile.Inspect(f);
                    if (info.ReadFailed) continue;
                    total += info.Rows;
                    var item = new ListViewItem(info.Name);
                    item.SubItems.Add(info.SizeBytes >= 1 << 20 ? $"{info.SizeBytes / (double)(1 << 20):0.0} MB" : $"{info.SizeBytes / 1024.0:0.0} KB");
                    item.SubItems.Add(info.Rows.ToString("N0", CultureInfo.InvariantCulture));
                    item.SubItems.Add(info.FirstTime.HasValue && info.LastTime.HasValue
                        ? $"{info.FirstTime:yyyy-MM-dd} ~ {info.LastTime:yyyy-MM-dd}"
                        : "-");
                    item.SubItems.Add(info.Modified.ToString("yyyy-MM-dd HH:mm"));
                    item.Tag = info;
                    listView.Items.Add(item);
                }
                dataStatusLabel.Text = $"{files.Count} 个文件,共 {total:N0} 行";
            }
            catch (Exception ex)
            {
                dataStatusLabel.Text = "读取目录失败: " + ex.Message;
            }
            listView.EndUpdate();

            if (selectName != null)
            {
                foreach (ListViewItem item in listView.Items)
                {
                    if (string.Equals(item.Text, selectName, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                        item.EnsureVisible();
                        break;
                    }
                }
            }
        }

        private CsvInfo SelectedCsv()
        {
            return listView.SelectedItems.Count > 0 ? listView.SelectedItems[0].Tag as CsvInfo : null;
        }

        private void LoadPreview(bool force = false)
        {
            var info = SelectedCsv();
            if (info == null)
            {
                grid.DataSource = null;
                chartRows = null;
                chartBox.Invalidate();
                previewInfoLabel.Text = "";
                return;
            }
            try
            {
                var rows = CsvFile.ReadRows(info.Path, maxRows: 100000);
                var view = rows.Take(500).Select(r => new
                {
                    时间 = r.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    开盘 = r.Open,
                    最高 = r.High,
                    最低 = r.Low,
                    收盘 = r.Close,
                    成交量 = r.Volume,
                    WAP = r.Wap,
                    笔数 = r.Count
                }).ToList();
                grid.DataSource = view;
                grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
                chartRows = rows;
                chartBox.Invalidate();
            }
            catch (Exception ex)
            {
                grid.DataSource = null;
                dataStatusLabel.Text = "预览失败: " + ex.Message;
            }
        }

        private void OpenSelected()
        {
            var info = SelectedCsv();
            if (info == null) return;
            try { Process.Start(new ProcessStartInfo(info.Path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
        }

        private void DeleteSelected()
        {
            var info = SelectedCsv();
            if (info == null) return;
            if (MessageBox.Show(this, $"确认删除 {info.Name} ?", "删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                File.Delete(info.Path);
                RefreshDataList();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除失败"); }
        }

        private void RenameSelected()
        {
            var info = SelectedCsv();
            if (info == null) return;
            var name = InputBox.Show(this, "重命名", "新文件名:", info.Name);
            if (name == null || name == info.Name) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length == 0)
            {
                MessageBox.Show(this, "文件名无效。", "重命名");
                return;
            }
            var newPath = Path.Combine(Path.GetDirectoryName(info.Path), name);
            try
            {
                File.Move(info.Path, newPath);
                RefreshDataList(selectName: name);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "重命名失败"); }
        }

        private void OpenFolderOfSelected()
        {
            var info = SelectedCsv();
            if (info == null) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{info.Path}\"") { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        // ================================================================ chart

        private void ChartBox_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            if (chartRows == null || chartRows.Count == 0)
            {
                using var f = new Font("Microsoft YaHei", 10f);
                g.DrawString("无数据", f, Brushes.Gray, 10, 10);
                return;
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rows = chartRows;

            double minC = rows.Min(r => r.Close), maxC = rows.Max(r => r.Close);
            decimal maxV = rows.Max(r => r.Volume);
            if (maxC == minC) { maxC += 1; minC -= 1; }
            if (maxV <= 0) maxV = 1;

            var rect = chartBox.ClientRectangle;
            int left = 55, right = rect.Width - 10, top = 10, bottom = rect.Height - 25;
            int priceBottom = top + (int)((bottom - top) * 0.72);
            int volTop = priceBottom + 8;

            using (var gridPen = new Pen(Color.FromArgb(230, 230, 230)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = top + (priceBottom - top) * i / 4;
                    g.DrawLine(gridPen, left, y, right, y);
                }
            }

            // close line
            var pts = new PointF[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                float x = left + (right - left) * (float)i / Math.Max(1, rows.Count - 1);
                float y = priceBottom - (float)((rows[i].Close - minC) / (maxC - minC)) * (priceBottom - top);
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(Color.SteelBlue, 1.5f))
                g.DrawLines(pen, pts);

            // price labels
            using (var f = new Font("Consolas", 8f))
            {
                g.DrawString(maxC.ToString("0.0000"), f, Brushes.Gray, 2, top - 4);
                g.DrawString(minC.ToString("0.0000"), f, Brushes.Gray, 2, priceBottom - 8);
                g.DrawString(rows[0].Time.ToString("yyyy-MM-dd HH:mm"), f, Brushes.Gray, left, bottom - 4);
                g.DrawString(rows[rows.Count - 1].Time.ToString("yyyy-MM-dd HH:mm"), f, Brushes.Gray, right - 110, bottom - 4);
                g.DrawString("收盘价", f, Brushes.DimGray, left, 2);
            }

            // volume bars
            using (var vp = new Pen(Color.FromArgb(160, 160, 160)))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    float x = left + (right - left) * (float)i / Math.Max(1, rows.Count - 1);
                    float h = (float)((double)rows[i].Volume / (double)maxV) * (bottom - volTop);
                    g.DrawLine(vp, x, bottom, x, bottom - Math.Max(1f, h));
                }
            }
            using (var f = new Font("Consolas", 8f))
                g.DrawString("成交量", f, Brushes.DimGray, left, volTop - 2);
        }

        // ================================================================ misc

        private void Log(string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => AppendLog(msg))); }
                catch { /* form closing */ }
            }
            else AppendLog(msg);
        }

        private void SafeLog(string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => AppendLog(msg))); }
                catch { }
            }
            else AppendLog(msg);
        }

        private void AppendLog(string msg)
        {
            if (logBox.TextLength > 200000) logBox.Clear();
            logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }
    }

    /// <summary>Minimal input dialog (for rename).</summary>
    internal sealed class InputBox : Form
    {
        private readonly TextBox box = new TextBox { Width = 260 };

        public InputBox(string title, string prompt, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(300, 90);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = prompt, AutoSize = true }, 0, 0);
            box.Text = initial;
            box.SelectAll();
            layout.Controls.Add(box, 0, 1);

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80 };
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            row.Controls.Add(cancel);
            row.Controls.Add(ok);
            layout.Controls.Add(row, 0, 2);

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (_, _) => box.Focus();
        }

        public static string Show(IWin32Window owner, string title, string prompt, string initial)
        {
            using var dlg = new InputBox(title, prompt, initial);
            return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.box.Text.Trim() : null;
        }
    }
}
