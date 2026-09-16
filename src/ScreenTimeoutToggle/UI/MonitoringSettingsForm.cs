using System.Text.Json;
using OBDim.Models;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;

namespace OBDim.UI;

public sealed record SettingsApplyResult(bool AppSaved, bool MonitoringSaved,
    bool ModeHotkeyApplied = true, bool PopoverHotkeyApplied = true,
    bool AutoStartApplied = true, bool PowerApplied = true)
{
    public bool Success => AppSaved && MonitoringSaved && ModeHotkeyApplied
        && PopoverHotkeyApplied && AutoStartApplied && PowerApplied;
}

/// <summary>A single persistent settings window. Controls own the draft until explicit save.</summary>
public sealed class MonitoringSettingsForm : Form
{
    private readonly MonitoringCoordinator _coordinator;
    private readonly Func<AppConfig>? _getApp;
    private readonly Func<AppConfig, MonitoringSettings, Task<SettingsApplyResult>>? _apply;
    private readonly ListBox _navigation = new() { Dock = DockStyle.Left, Width = 190, BorderStyle = BorderStyle.None };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly List<Panel> _pages = [];
    private readonly List<(Control Control, Func<string> Text)> _texts = [];
    private readonly NumericUpDown[] _timeouts = Enumerable.Range(0, 4)
        .Select(_ => new TimeoutMinutesBox { Minimum = 0, Maximum = 99999, Width = 140 }).ToArray();
    private readonly MonitorForm.HotkeyCaptureBox _modeHotkey = new() { Width = 230, ReadOnly = true };
    private readonly MonitorForm.HotkeyCaptureBox _panelHotkey = new() { Width = 230, ReadOnly = true };
    private readonly CheckBox _leftClick = new() { AutoSize = true };
    private readonly CheckBox _memory = new() { AutoSize = true };
    private readonly CheckBox _autoStart = new() { AutoSize = true };
    private readonly CheckBox _reminders = new() { AutoSize = true };
    private readonly CheckBox _agentPlan = new() { Text = "Agent Plan", AutoSize = true };
    private readonly CheckBox _codingPlan = new() { Text = "Coding Plan", AutoSize = true };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _globalInterval = IntervalBox(false);
    private readonly Dictionary<ProviderId, CheckBox> _enabled = [];
    private readonly Dictionary<ProviderId, TextBox> _paths = [];
    private readonly Dictionary<ProviderId, ComboBox> _intervals = [];
    private readonly Dictionary<ProviderId, Label> _effectiveIntervals = [];
    private readonly Button _save = new() { Name = "save", AutoSize = true, MinimumSize = new Size(110, 34) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(850, 0) };
    private AppConfig _baselineApp = AppConfig.CreateDefault();
    private MonitoringSettings _baselineMonitoring = new();
    private string _savedFingerprint = "";
    private bool _saving;
    private readonly Dictionary<ProviderId, Label> _providerStatuses = [];
    private readonly Dictionary<ProviderId, Label> _providerActions = [];
    private readonly Dictionary<ProviderId, Button> _detectButtons = [];
    private readonly Dictionary<ProviderId, Button> _loginButtons = [];
    private readonly Dictionary<ProviderId, CancellationTokenSource> _loginOperations = [];
    private int _pendingProviderUi;
    internal Func<ProviderSettings,CancellationToken,Task<int>> LoginRunner { get; set; } = CliLoginLauncher.LoginAsync;
    internal Func<bool>? ConfirmDiscard { get; set; }
    internal bool HasUnsavedChanges => Fingerprint() != _savedFingerprint;

    private static bool English => LocalizationService.CurrentLanguage == "en-US";
    private static string T(string zh, string en) => English ? en : zh;
    private sealed class TimeoutMinutesBox : NumericUpDown
    {
        protected override void UpdateEditText()
        {
            // UpDownBase must know this text is formatting, not new user input.
            // Otherwise reading Value re-enters validation through the Never label.
            UserEdit = false;
            ChangingText = true;
            Text = Value == 0 ? T("从不", "Never") : Value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
        protected override void ValidateEditText()
        {
            if (Text is "从不" or "Never") { UserEdit = false; Value = 0; UpdateEditText(); }
            else base.ValidateEditText();
        }
        public void RefreshLanguage() => UpdateEditText();
    }

    private sealed record IntervalChoice(int? Minutes)
    {
        public override string ToString() => Minutes switch
        {
            null => T("跟随默认", "Use default"),
            0 => T("仅手动", "Manual only"),
            var value => T($"{value} 分钟", $"{value} min"),
        };
    }

    public MonitoringSettingsForm(MonitoringCoordinator coordinator, Func<AppConfig>? getApp = null,
        Func<AppConfig, MonitoringSettings, Task<SettingsApplyResult>>? apply = null)
    {
        _coordinator = coordinator; _getApp = getApp; _apply = apply;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;
        ClientSize = new Size(960, 780);
        MinimumSize = new Size(640, 460);
        StartPosition = FormStartPosition.CenterScreen;
        _navigation.BackColor = Color.FromArgb(247, 249, 252);
        _navigation.DrawMode = DrawMode.OwnerDrawFixed;
        _navigation.ItemHeight = (int)Math.Round(48 * DeviceDpi / 96.0);
        _navigation.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(234, 243, 255) : _navigation.BackColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            var bounds = Rectangle.Inflate(e.Bounds, -14, 0);
            TextRenderer.DrawText(e.Graphics, _navigation.Items[e.Index].ToString(), Font, bounds,
                selected ? Color.FromArgb(23, 119, 236) : Color.FromArgb(66, 74, 86),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        _navigation.IntegralHeight = false;
        _navigation.SelectedIndexChanged += (_, _) => SelectCategory(_navigation.SelectedIndex);
        _language.Items.AddRange(["中文", "English"]);
        _globalInterval.SelectedIndexChanged += (_, _) => UpdateEffectiveIntervals();

        BuildScreen(); BuildProviders(); BuildNotifications(); BuildGeneral(); BuildAdvanced();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 90),
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 10, 20, 12) };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        Bind(_save, "保存", "Save");
        _save.Click += async (_, _) => await SaveDraftAsync();
        var discard = new Button { AutoSize = true, MinimumSize = new Size(110, 34) };
        Bind(discard, "撤销修改", "Discard changes");
        discard.Click += (_, _) => { LoadDraft(); _status.Text = ""; };
        buttons.Controls.AddRange([_save, discard]);
        footer.Controls.Add(buttons); footer.Controls.Add(_status);
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_body); content.Controls.Add(footer);
        Controls.Add(content); Controls.Add(_navigation);
        LoadDraft(); RefreshLanguage(); SelectCategory(0);
        _coordinator.QuotaStateChanged += OnProviderStateChanged;
        RefreshProviderStatuses();
    }

    protected override void OnShown(EventArgs e)
    {
        var work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
        Size = new Size(Math.Min(Width, work.Width - 16), Math.Min(Height, work.Height - 16));
        Location = new Point(Math.Clamp(Left, work.Left, work.Right - Width), Math.Clamp(Top, work.Top, work.Bottom - Height));
        base.OnShown(e);
        RefreshProviderStatuses();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            if (_saving) e.Cancel = true;
            else if (HasUnsavedChanges)
                e.Cancel = !(ConfirmDiscard?.Invoke() ?? (MessageBox.Show(this,
                    T("放弃未保存的修改并关闭？", "Discard unsaved changes and close?"), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
        }
        base.OnFormClosing(e);
    }

    internal void SelectCategory(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        for (var i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        if (_navigation.SelectedIndex != index) _navigation.SelectedIndex = index;
    }

    private TableLayoutPanel Page(string zh, string en)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 18, 24, 18) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = Label(zh, en); title.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        Add(table, title, 12);
        scroll.Controls.Add(table); _pages.Add(scroll); _body.Controls.Add(scroll);
        return table;
    }

    private TControl Bind<TControl>(TControl c, string zh, string en) where TControl : Control
    {
        _texts.Add((c, () => T(zh, en))); c.Text = T(zh, en); return c;
    }
    private Label Label(string zh, string en) => Bind(new Label { AutoSize = true, MaximumSize = new Size(620, 0) }, zh, en);
    private static void Add(TableLayoutPanel table, Control c, int bottom = 10)
    {
        c.Margin = new Padding(0, 3, 0, bottom);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(c, 0, table.RowCount++);
    }
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
        row.Controls.AddRange(controls); return row;
    }
    private static ComboBox IntervalBox(bool inherit)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        if (inherit) box.Items.Add(new IntervalChoice(null));
        foreach (var minutes in new[] { 1, 5, 15, 30, 0 }) box.Items.Add(new IntervalChoice(minutes));
        return box;
    }
    private static void SetInterval(ComboBox box, int? value) =>
        box.SelectedItem = box.Items.Cast<IntervalChoice>().First(c => c.Minutes == value);
    private static int? Interval(ComboBox box) => (box.SelectedItem as IntervalChoice)?.Minutes;

    private void BuildScreen()
    {
        var page = Page("息屏与模式", "Display and modes");
        Add(page, Label("仅修改显示器息屏时间，不改变系统睡眠或锁屏。分钟数 0 表示从不。", "Changes display timeout only. Sleep and lock settings are unchanged. 0 minutes means never."));
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
        grid.Controls.Add(Label("模式", "Mode"), 0, 0);
        grid.Controls.Add(Label("接通电源（分钟）", "Plugged in (minutes)"), 1, 0);
        grid.Controls.Add(Label("使用电池（分钟）", "On battery (minutes)"), 2, 0);
        grid.Controls.Add(Label("工作", "Work"), 0, 1);
        grid.Controls.Add(Label("离开", "Away"), 0, 2);
        for (var i = 0; i < 4; i++)
        {
            _timeouts[i].Name = new[] { "workAc", "workDc", "awayAc", "awayDc" }[i];
            _timeouts[i].AccessibleName = _timeouts[i].Name;
            grid.Controls.Add(_timeouts[i], 1 + i % 2, 1 + i / 2);
        }
        Add(page, grid);
        Add(page, Bind(_leftClick, "托盘左键打开额度与内存面板（关闭则切换工作／离开）", "Left click opens usage and memory (off: switch Work/Away)"));
    }

    private void BuildProviders()
    {
        var page = Page("额度来源", "Quota sources");
        Add(page, Label("默认自动刷新频率", "Default refresh interval")); Add(page, _globalInterval);
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            var enable = new CheckBox { Name = $"enable_{id}", Text = id.ToString(), AutoSize = true };
            _enabled[id] = enable; Add(page, enable, 3);
            var cadence = IntervalBox(true); cadence.Name = $"interval_{id}"; _intervals[id] = cadence;
            var effective = new Label { AutoSize = true, Padding = new Padding(6, 5, 0, 0) }; _effectiveIntervals[id] = effective;
            cadence.SelectedIndexChanged += (_, _) => UpdateEffectiveIntervals();
            Add(page, Row(cadence, effective), 3);
            var path = new TextBox { Name = $"cliPath_{id}", Width = 360 }; _paths[id] = path;
            var advanced = new Panel { AutoSize = true, Dock = DockStyle.Top, Visible = false };
            var details = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1 };
            Add(details, Label("CLI 程序路径（可选）；留空自动检测，不是 API 网址。", "CLI program path (optional). Leave blank for auto-detection; this is not an API URL."), 3);
            var browse = Bind(new Button { AutoSize = true }, "选择文件…", "Browse…");
            browse.Click += (_, _) =>
            {
                using var picker = new OpenFileDialog { Filter = "CLI (*.exe;*.cmd)|*.exe;*.cmd|All files|*.*" };
                if (picker.ShowDialog(this) == DialogResult.OK) path.Text = picker.FileName;
            };
            Add(details, Row(path, browse), 3); advanced.Controls.Add(details);
            var expand = Bind(new Button { AutoSize = true }, "CLI 路径…", "CLI path…");
            expand.Click += (_, _) => advanced.Visible = !advanced.Visible;
            var detect = Bind(new Button { Name=$"check_{id}",AutoSize = true }, "重新检测", "Check connection");
            var login = new Button { Name=$"login_{id}",AutoSize=true };
            var status = new Label { Name=$"connection_{id}",AutoSize=true,MaximumSize=new Size(590,0) };
            var action = new Label { Name=$"action_{id}",AutoSize=true,MaximumSize=new Size(590,0),ForeColor=Color.FromArgb(145,100,21) };
            _providerStatuses[id]=status; _providerActions[id]=action;
            _detectButtons[id]=detect; _loginButtons[id]=login;
            detect.Click += (_,_) => CheckProvider(id);
            login.Click += async (_,_) => await LoginProviderAsync(id);
            path.TextChanged += (_,_) => RefreshProviderStatuses();
            enable.CheckedChanged += (_,_) => RefreshProviderStatuses();
            Add(page,Row(expand,detect,login),3); Add(page,advanced,3); Add(page,status,3); Add(page,action,12);
        }
        Add(page, Label("火山方舟产品显示（不改变订阅状态）", "Ark product visibility (does not change subscriptions)"));
        Add(page, Row(_agentPlan, _codingPlan));
    }

    private readonly HashSet<ProviderId> _awaitingVerification=[];

    private bool UsesSavedProvider(ProviderId id)
    {
        var saved=_coordinator.Settings.Provider(id);
        return _enabled[id].Checked==saved.Enabled &&
            string.Equals(_paths[id].Text.Trim(),saved.CliPath?.Trim()??"",StringComparison.Ordinal);
    }

    internal void CheckProvider(ProviderId id)
    {
        if (!UsesSavedProvider(id))
        { _providerActions[id].Text=T("请先保存来源开关或 CLI 路径的修改，再检测。","Save provider or CLI-path changes before checking."); return; }
        var started=_coordinator.RequestManualRefresh(id);
        _providerActions[id].Text=started?"":T("已有检测正在运行，或需等待 15 秒冷却后重试。","A check is running, or the 15-second retry cooldown has not elapsed.");
        RefreshProviderStatuses();
    }

    internal async Task LoginProviderAsync(ProviderId id)
    {
        if (_loginOperations.TryGetValue(id,out var active)) { active.Cancel(); return; }
        if (!UsesSavedProvider(id))
        { _providerActions[id].Text=T("请先保存来源开关或 CLI 路径的修改，再登录。","Save provider or CLI-path changes before signing in."); return; }
        if (!_coordinator.BeginAuthentication(id)) return;
        using var cancellation=new CancellationTokenSource();
        _loginOperations[id]=cancellation;
        var verify=false;
        _providerActions[id].Text=T("已启动官方登录；请完成 CLI 窗口或浏览器中的操作。","Official sign-in started; complete the CLI or browser flow.");
        RefreshProviderStatuses();
        try
        {
            var settings=_coordinator.Settings.Provider(id);
            var exit=await Task.Run(()=>LoginRunner(settings,cancellation.Token));
            verify=true; // fresh provider response, not exit code, determines success
            if (!IsDisposed) _providerActions[id].Text=exit==0
                ?T("登录程序已结束，正在重新验证连接与额度…","Sign-in process finished; verifying connection and quota…")
                :T($"登录程序返回退出码 {exit}，正在核查授权是否仍已完成…",$"Sign-in exited with code {exit}; checking whether authorization completed…");
        }
        catch(OperationCanceledException)
        { if (!IsDisposed) _providerActions[id].Text=T("登录已取消或超时；不会把此次操作标记为成功。","Sign-in cancelled or timed out; no success was assumed."); }
        catch(Exception)
        { if (!IsDisposed) _providerActions[id].Text=T("无法启动官方登录。请检查 CLI 路径及版本后重试。","Could not start official sign-in. Check the CLI path and version."); }
        finally
        {
            _loginOperations.Remove(id);
            if(verify && !IsDisposed) _awaitingVerification.Add(id);
            _coordinator.EndAuthentication(id,verify && !IsDisposed);
            if (!IsDisposed) RefreshProviderStatuses();
        }
    }

    private void OnProviderStateChanged()
    {
        if (IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref _pendingProviderUi,1)==1) return;
        try { BeginInvoke((Action)(()=> { Interlocked.Exchange(ref _pendingProviderUi,0); if(!IsDisposed) RefreshProviderStatuses(); })); }
        catch(InvalidOperationException) { Interlocked.Exchange(ref _pendingProviderUi,0); }
    }

    private void RefreshProviderStatuses()
    {
        foreach(var (id,label) in _providerStatuses)
        {
            var state=_coordinator.GetDisplayState(id);
            if(_awaitingVerification.Contains(id) && !state.Refreshing && (state.LastAttempt is not null || state.LastGood is not null))
            { _awaitingVerification.Remove(id); _providerActions[id].Text=""; }
            var text=ProviderStatusPresentation.Connection(state,English);
            var matches=UsesSavedProvider(id);
            label.Text=matches?text.Title+"\n"+text.Detail:T("来源设置尚未保存。保存后可检测或登录。","Provider changes are unsaved. Save before checking or signing in.");
            label.ForeColor=state.LastAttempt?.HasError==true?Color.FromArgb(166,63,38):Color.FromArgb(66,74,86);
            var logging=_loginOperations.ContainsKey(id);
            _loginButtons[id].Text=logging?T("取消登录","Cancel sign-in"):
                text.NeedsLogin?T("重新登录…","Sign in again…"):T("登录／重新授权…","Sign in / reauthorize…");
            _loginButtons[id].Enabled=logging || state.Enabled;
            _detectButtons[id].Enabled=state.Enabled&&!state.Refreshing&&!state.Authenticating;
            _paths[id].Enabled=!logging; _enabled[id].Enabled=!logging;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            _coordinator.QuotaStateChanged-=OnProviderStateChanged;
            foreach(var cancellation in _loginOperations.Values.ToArray()) cancellation.Cancel();
        }
        base.Dispose(disposing);
    }

    private void BuildNotifications()
    {
        var page = Page("通知", "Notifications");
        Add(page, Bind(_reminders, "启用本机低额度提醒", "Enable local low-quota reminders"));
        Add(page, Label("剩余额度达到 20% 或 5% 时显示系统托盘气泡。同一周期去重，仅使用新鲜且已确认身份的数据。", "Shows a tray balloon at 20% or 5% remaining. Alerts are deduplicated per cycle and require fresh, verified account data."));
    }

    private void BuildGeneral()
    {
        var page = Page("通用与快捷键", "General and shortcuts");
        Add(page, Label("语言", "Language")); Add(page, _language);
        Add(page, Bind(_autoStart, "开机自动启动", "Start with Windows"));
        Add(page, Bind(_memory, "启用内存监控（每 5 秒采样）", "Enable memory monitoring (every 5 seconds)"));
        Add(page, Label("切换工作／离开快捷键", "Switch Work/Away shortcut")); Add(page, _modeHotkey);
        Add(page, Label("打开额度与内存面板快捷键", "Open usage and memory shortcut")); Add(page, _panelHotkey);
        Add(page, Label("点击输入框后按下快捷键；两个快捷键必须不同。", "Focus an input and press the shortcut. The two shortcuts must be different."));
    }

    private void BuildAdvanced()
    {
        var page = Page("高级与关于", "Advanced and about");
        Add(page, new Label { AutoSize = true, Text = "OB Dim " + Application.ProductVersion });
        var clear = Bind(new Button { AutoSize = true }, "清除额度缓存", "Clear quota cache");
        clear.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, T("清除额度缓存？登录凭据、息屏配置和内存历史会保留。", "Clear quota cache? Credentials, display settings and memory history are kept."), Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            clear.Enabled = false;
            try
            {
                var success = await _coordinator.ClearQuotaCacheAsync();
                if (!IsDisposed) _status.Text = success ? T("额度缓存已清除。", "Quota cache cleared.") : T("部分缓存未能清除，请检查文件权限。", "Some cache files could not be cleared. Check permissions.");
            }
            finally { if (!IsDisposed) clear.Enabled = true; }
        };
        Add(page, clear);
        var export = Bind(new Button { AutoSize = true }, "导出脱敏诊断…", "Export sanitized diagnostics…");
        export.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "obdim-diagnostics.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dialog.FileName, _coordinator.ExportDiagnostics()); _status.Text = T("诊断已导出。", "Diagnostics exported."); }
            catch (Exception) { _status.Text = T("导出失败，请检查保存位置。", "Export failed. Check the destination."); }
        };
        Add(page, export);
        Add(page, Label("诊断仅包含版本、来源开关、错误分类和数据时效，不包含 CLI 路径、账号、凭据或原始响应。", "Diagnostics contain only version, provider switches, error classifications and freshness. CLI paths, accounts, credentials and raw responses are excluded."));
    }

    private void LoadDraft()
    {
        _baselineApp = _getApp?.Invoke() ?? AppConfig.CreateDefault();
        _baselineMonitoring = _coordinator.Settings;
        var values = new[] { _baselineApp.Work.AcMinutes, _baselineApp.Work.DcMinutes, _baselineApp.Away.AcMinutes, _baselineApp.Away.DcMinutes };
        for (var i = 0; i < values.Length; i++) _timeouts[i].Value = Math.Clamp(values[i], 0, 99999);
        _modeHotkey.SetCaptured(_baselineApp.Hotkey);
        _panelHotkey.SetCaptured(GlobalHotkeyService.ParseHotkeyString(_baselineMonitoring.PopoverHotkey) ?? new HotkeyConfig { Key = "D" });
        _language.SelectedIndex = _baselineApp.Language == "en-US" ? 1 : 0;
        _autoStart.Checked = _baselineApp.AutoStart; _memory.Checked = _baselineMonitoring.MemoryEnabled;
        _leftClick.Checked = _baselineMonitoring.LeftClickOpensPopover; _reminders.Checked = _baselineMonitoring.RemindersEnabled;
        _agentPlan.Checked = _baselineMonitoring.ShowArkAgentPlan; _codingPlan.Checked = _baselineMonitoring.ShowArkCodingPlan;
        SetInterval(_globalInterval, _baselineMonitoring.RefreshIntervalMinutes);
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            var settings = _baselineMonitoring.Provider(id);
            _enabled[id].Checked = settings.Enabled; _paths[id].Text = settings.CliPath ?? "";
            SetInterval(_intervals[id], settings.RefreshIntervalMinutes);
        }
        UpdateEffectiveIntervals(); _savedFingerprint = Fingerprint();
        RefreshProviderStatuses();
    }

    private AppConfig DraftApp() => _baselineApp with
    {
        Work = new TimeoutConfig { AcMinutes = (int)_timeouts[0].Value, DcMinutes = (int)_timeouts[1].Value },
        Away = new TimeoutConfig { AcMinutes = (int)_timeouts[2].Value, DcMinutes = (int)_timeouts[3].Value },
        Hotkey = _modeHotkey.Captured, AutoStart = _autoStart.Checked,
        Language = _language.SelectedIndex == 1 ? "en-US" : "zh-CN",
    };
    private MonitoringSettings DraftMonitoring() => _baselineMonitoring with
    {
        MemoryEnabled = _memory.Checked, RemindersEnabled = _reminders.Checked, LeftClickOpensPopover = _leftClick.Checked,
        PopoverHotkey = $"{_panelHotkey.Captured.Modifiers}+{_panelHotkey.Captured.Key}",
        RefreshIntervalMinutes = Interval(_globalInterval) ?? 5,
        ShowArkAgentPlan = _agentPlan.Checked, ShowArkCodingPlan = _codingPlan.Checked,
        Providers = Enum.GetValues<ProviderId>().Select(id => _baselineMonitoring.Provider(id) with
        {
            Enabled = _enabled[id].Checked, CliPath = string.IsNullOrWhiteSpace(_paths[id].Text) ? null : _paths[id].Text.Trim(),
            RefreshIntervalMinutes = Interval(_intervals[id]),
        }).ToList(),
    };
    private string Fingerprint() => JsonSerializer.Serialize(new { App = DraftApp(), Monitoring = DraftMonitoring() });

    internal async Task SaveDraftAsync()
    {
        if (_saving) return;
        var app = DraftApp(); var monitoring = DraftMonitoring();
        if (HotkeyService.KeyStringToVk(app.Hotkey.Key) == HotkeyService.KeyStringToVk(_panelHotkey.Captured.Key)
            && HotkeyService.ParseModifiers(app.Hotkey.Modifiers) == HotkeyService.ParseModifiers(_panelHotkey.Captured.Modifiers))
        { _status.Text = T("两个快捷键不能相同。", "The two shortcuts must be different."); return; }
        _saving = true; _save.Enabled = false; _body.Enabled = false; _navigation.Enabled = false;
        _status.Text = T("正在保存并应用…", "Saving and applying…");
        try
        {
            SettingsApplyResult result;
            if (_apply is not null) result = await _apply(app, monitoring);
            else
            {
                var saved = _coordinator.SaveSettings(monitoring);
                if (saved) _coordinator.ApplySettings(monitoring);
                result = new SettingsApplyResult(true, saved);
            }
            if (IsDisposed) return;
            RefreshLanguage();
            var failures = new List<string>();
            if (!result.AppSaved) failures.Add(T("息屏配置保存失败", "Display configuration could not be saved"));
            if (!result.MonitoringSaved) failures.Add(T("监控配置保存失败", "Monitoring configuration could not be saved"));
            if (!result.ModeHotkeyApplied) failures.Add(T("模式热键未生效", "Mode shortcut not applied"));
            if (!result.PopoverHotkeyApplied) failures.Add(T("面板热键未生效", "Panel shortcut not applied"));
            if (!result.AutoStartApplied) failures.Add(T("自启动未生效", "Autostart not applied"));
            if (!result.PowerApplied) failures.Add(T("系统息屏时间未生效", "System display timeout not applied"));
            _status.Text = result.Success ? T("配置已保存，设置已生效。", "Configuration saved and settings applied.") : string.Join("；", failures);
            if (result.Success) _savedFingerprint = Fingerprint();
        }
        catch (Exception)
        { if (!IsDisposed) _status.Text = T("保存或应用失败；草稿已保留，请重试。", "Save or apply failed. Your draft is preserved; please retry."); }
        finally
        {
            _saving = false;
            if (!IsDisposed) { _save.Enabled = true; _body.Enabled = true; _navigation.Enabled = true; }
        }
    }

    private void RefreshLanguage()
    {
        Text = T("OB Dim 设置", "OB Dim Settings");
        foreach (var (control, text) in _texts) control.Text = text();
        foreach (var timeout in _timeouts.OfType<TimeoutMinutesBox>()) timeout.RefreshLanguage();
        var index = Math.Max(0, _navigation.SelectedIndex);
        _navigation.Items.Clear();
        _navigation.Items.AddRange(English
            ? ["Display and modes", "Quota sources", "Notifications", "General and shortcuts", "Advanced and about"]
            : ["息屏与模式", "额度来源", "通知", "通用与快捷键", "高级与关于"]);
        _navigation.SelectedIndex = index;
        foreach (var box in _intervals.Values.Append(_globalInterval))
        {
            var selected = Interval(box); var choices = box.Items.Cast<IntervalChoice>().ToArray();
            box.Items.Clear(); box.Items.AddRange(choices); SetInterval(box, selected);
        }
        UpdateEffectiveIntervals();
    }

    private void UpdateEffectiveIntervals()
    {
        foreach (var (id, label) in _effectiveIntervals)
        {
            var value = Interval(_intervals[id]) ?? Interval(_globalInterval) ?? 5;
            label.Text = T("实际频率：", "Effective: ") + new IntervalChoice(value);
        }
    }
}
