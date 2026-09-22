using OBDim.Monitoring.Network.V2;
using OBDim.Services;

namespace OBDim.UI.Network;

/// <summary>Persistent view over an app-owned source. Hidden views never rebuild charts.</summary>
public sealed class NetworkPage : UserControl
{
    private readonly INetworkObservationSource _source;
    private readonly Func<bool, Task<bool>> _enable;
    private readonly Label _saveStatus = new();
    private readonly Button _retry = new();
    private bool _saveFailed;
    private int _saveRequest;
    private readonly Panel _body = new();
    private readonly Label _identity = new(), _status = new(), _scope = new(), _history = new(), _updated = new(), _limited = new(), _details = new();
    private readonly Button _toggle = new(), _diagnostics = new();
    private readonly ComboBox _interface = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly System.Windows.Forms.Timer _freshness = new() { Interval = 1000 };
    public NetworkTrendControl Chart { get; } = new();
    private bool _binding, _detailsVisible;
    private int _queued;
    private string _selection = "auto", _interfaceSignature = "";
    private string _language = "";
    private static bool English => LocalizationService.CurrentLanguage == "en-US";
    private static string T(string zh, string en) => English ? en : zh;
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    public string Selection => _selection;
    public NetworkPage(INetworkObservationSource source, Func<bool, bool> enable)
        : this(source, enabled => Task.FromResult(enable(enabled))) { }
    public NetworkPage(INetworkObservationSource source, Func<bool, Task<bool>> enable)
    {
        _source = source; _enable = enable;
        Name = "network-page"; AccessibleName = "网络"; BackColor = Color.White; AutoScroll = true;
        Font = new Font("Microsoft YaHei UI", 9F); _body.BackColor = Color.White;
        foreach (var l in new[] { _identity, _status, _scope, _history, _updated, _limited, _details })
        { l.AutoSize = false; l.ForeColor = Color.FromArgb(105, 114, 127); _body.Controls.Add(l); }
        _identity.BackColor = Color.FromArgb(246, 247, 249);
        _status.Font = new Font(Font, FontStyle.Bold); _status.ForeColor = Color.FromArgb(48, 54, 65);
        _limited.BackColor = Color.FromArgb(246, 247, 249); _limited.Padding = new Padding(8);
        foreach (var b in new[] { _toggle, _diagnostics })
        { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(231, 233, 238); b.Cursor = Cursors.Hand; _body.Controls.Add(b); }
        _toggle.Name = "network-toggle"; _diagnostics.Name = "network-diagnostics";
        _interface.Name = "network-interface"; _interface.AccessibleName = T("观察网络接口", "Observed network interface");
        _interface.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _interface.SelectedItem is not Choice c) return;
            _selection = c.Id; Render();
        };
        _saveStatus.Name = "network-save-status";
        _saveStatus.ForeColor = Color.FromArgb(160, 70, 30);
        _retry.Name = "network-save-retry";
        _body.Controls.Add(_saveStatus); _body.Controls.Add(_retry);
        _toggle.Click += async (_, _) => await SetEnabledAsync(!_source.IsRunning);
        _retry.Click += async (_, _) => await SetEnabledAsync(_source.IsRunning);
        _diagnostics.Click += (_, _) => { _detailsVisible = !_detailsVisible; LayoutPage(); Render(); };
        _body.Controls.Add(_interface); _body.Controls.Add(Chart); Controls.Add(_body);
        _source.Changed += SourceChanged;
        _freshness.Tick += (_, _) => Render();
        VisibleChanged += (_, _) => { if (Visible) { Render(); _freshness.Start(); } else _freshness.Stop(); };
        LayoutPage();
    }
    internal async Task SetEnabledAsync(bool enabled)
    {
        var request = ++_saveRequest;
        var task = _enable(enabled); // applies runtime state before returning the save task
        Render();
        bool saved;
        try { saved = await task; } catch (Exception) { saved = false; }
        if (IsDisposed || request != _saveRequest) return;
        _saveFailed = !saved;
        LayoutPage(); Render();
    }
    public bool OwnsKey(Keys key) =>
        (Chart.Focused || Chart.RangeButtons.Any(b => b.Focused)) && key is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Space
        || key == Keys.Escape && (_interface.DroppedDown || Chart.HasReading);
    public bool HandleEscape()
    {
        if (_interface.DroppedDown) { _interface.DroppedDown = false; return true; }
        if (Chart.HasReading) { Chart.ClearReading(); return true; }
        return false;
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutPage(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); LayoutPage(); }
    private void LayoutPage()
    {
        if (_body is null) return;
        _body.Location = AutoScrollPosition;
        _body.Width = Math.Max(S(300), ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        _body.Height = S(_detailsVisible ? 966 : 762);
        var w = _body.Width; var left = S(16); var inner = w - S(32);
        _identity.SetBounds(0, 0, w, S(28));
        _status.SetBounds(left, S(42), inner - S(100), S(25));
        _scope.SetBounds(left, S(67), inner, S(25));
        _toggle.SetBounds(w - left - S(94), S(43), S(94), S(32));
        _interface.SetBounds(left, S(100), inner, S(30));
        Chart.SetBounds(left, S(144), inner, S(553));
        _history.SetBounds(left, S(699), inner, S(25));
        _limited.SetBounds(left, S(730), inner, S(60));
        _updated.SetBounds(left, S(801), inner - S(112), S(26));
        _diagnostics.SetBounds(w - left - S(104), S(798), S(104), S(30));
        _details.SetBounds(left, S(840), inner, S(160)); _details.Visible = _detailsVisible;
        var noticeTop = _detailsVisible ? 1008 : 840;
        _saveStatus.SetBounds(left, S(noticeTop), inner - S(86), S(58));
        _retry.SetBounds(w - left - S(80), S(noticeTop + 8), S(80), S(32));
        _saveStatus.Visible = _retry.Visible = _saveFailed;
        _body.Height = S((_detailsVisible ? 1016 : 844) + (_saveFailed ? 72 : 0));
    }
    private void SourceChanged()
    {
        if (IsDisposed || !IsHandleCreated || !Visible || Interlocked.Exchange(ref _queued, 1) != 0) return;
        try { BeginInvoke(() => { Interlocked.Exchange(ref _queued, 0); if (!IsDisposed && Visible) Render(); }); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _queued, 0); }
    }
    public void Render()
    {
        var snapshot = _source.Current; var now = DateTimeOffset.UtcNow;
        if (_language != LocalizationService.CurrentLanguage)
        { _language = LocalizationService.CurrentLanguage; _interfaceSignature = ""; Chart.Localize(); }
        var signature = _language + snapshot.SystemInterfaceID + string.Join("|", snapshot.Interfaces.Select(i => i.Id + i.Name + i.Available));
        if (_interfaceSignature != signature)
        {
            _binding = true;
            try
            {
                var route = snapshot.Interfaces.FirstOrDefault(i => i.Id == snapshot.SystemInterfaceID);
                _interface.Items.Clear();
                _interface.Items.Add(new Choice("auto", T("自动", "Auto") + " · " + (route?.Name ?? T("系统网络未确认", "System route unconfirmed"))));
                foreach (var i in snapshot.Interfaces) _interface.Items.Add(new Choice(i.Id,
                    i.Name + (i.Available ? "" : T(" · 不可用", " · unavailable"))));
                if (_selection != "auto" && !snapshot.Interfaces.Any(i => i.Id == _selection))
                    _interface.Items.Add(new Choice(_selection, T("手动接口 · 不可用", "Manual interface · unavailable")));
                _interface.SelectedIndex = _interface.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == _selection);
                _interfaceSignature = signature;
            }
            finally { _binding = false; }
        }
        var id = _selection == "auto" ? snapshot.SystemInterfaceID : _selection;
        var selected = snapshot.Interfaces.FirstOrDefault(i => i.Id == id);
        var fresh = snapshot.State == "active" && selected?.Available == true
            && selected.SampledAt is { } at && now - at <= TimeSpan.FromSeconds(5) && now >= at;
        var state = snapshot.State switch
        {
            "stopped" => T("已停止", "Stopped"),
            "starting" => T("正在启动", "Starting"),
            "waiting" => T("等待上次系统读取结束", "Waiting for the previous OS read"),
            "stalled" => T("系统读取超时 · 可停止采集", "OS read timed out · Stop is available"),
            "denied" => T("系统拒绝读取", "Read denied"),
            "disconnected" => T("采集源未连接", "Source disconnected"),
            _ when selected is null => T("观察接口未确认", "Interface unconfirmed"),
            _ when !selected.Available => T("所选接口不可用", "Selected interface unavailable"),
            _ when !fresh => T("旧数据 · 等待新采样", "Stale · waiting for a sample"),
            _ when selected.Rates.Upload is null || selected.Rates.Download is null => T("采集中 · 等待有效区间", "Collecting · awaiting a valid interval"),
            _ => T("采集中", "Collecting"),
        };
        _identity.Text = T("  本机数据 · 接口级观察", "  Native data · interface observation");
        _status.Text = state;
        _saveStatus.Text = (_source.IsRunning ? T("本次已启动；", "Running for this session; ") : T("本次已停止；", "Stopped for this session; "))
            + T("下次启动偏好未保存，请重试。", "startup preference was not saved. Please retry.");
        _retry.Text = T("重试保存", "Retry save");
        _scope.Text = _saveFailed ? T("偏好保存失败 · 本次启停已生效；下方可重试", "Preference not saved · session changed; retry below")
            : T("仅监控 · 防护未开启", "Monitoring only · protection off");
        _toggle.Text = _source.IsRunning ? T("停止采集", "Stop") : T("开始采集", "Start");
        _toggle.AccessibleName = _toggle.Text;
        _limited.Text = T("按应用统计尚未接入\n当前仅观察单个接口；没有应用、目标统计或连接阻断。",
            "Per-app statistics are not connected\nOne interface only; no app/target attribution or blocking.");
        _history.Text = snapshot.InterfaceCoverageLimited
            ? $"{T("接口覆盖受限", "Limited interface coverage")} {snapshot.RetainedInterfaceCount}/{snapshot.SourceInterfaceCount}"
                + (selected?.HistoryTruncated == true ? T(" · 历史也已截断", " · history also truncated") : "")
            : selected?.HistoryTruncated == true ? T("历史受资源上限截断 · 详情见统计说明", "History truncated by resource limit · see statistics")
            : selected?.Samples.Any(s => s.Continuity.Upload.Reason is not (null or "start") || s.Continuity.Download.Reason is not (null or "start")) == true
                ? T("历史含缺口 · 未补零或连接断点", "History contains gaps · no fabricated samples")
                : T("仅显示已采集的历史", "Only collected history is shown");
        _updated.Text = selected?.SampledAt is { } sampled ? $"{T("数据时间", "Sampled")} {sampled.LocalDateTime:HH:mm:ss}" : T("尚无样本", "No samples yet");
        _diagnostics.Text = T("统计说明", "Statistics");
        _details.Text = selected is null ? T("当前接口未确认；可手动选择接口。", "No confirmed interface; select one manually.") :
            $"{selected.Name}\nID: {selected.Id}\n{T("类型", "Kind")}: {selected.Kind}\n" +
            $"{T("原始上传 / 下载", "Raw sent / received")}: {selected.RawCounters.Upload?.ToString() ?? "?"} / {selected.RawCounters.Download?.ToString() ?? "?"} B\n" +
            $"{T("本接口方向重建基线次数", "Directional re-baselines on this interface")}: {selected.ResetCount}\n" +
            T("累计仅为可证实的观察段，各方向独立起点。\n自动：系统至 1.1.1.1 / IPv6 的路由查询，不发送数据；不代表全部互联网用量。",
                "Totals cover confirmed segments with independent starts.\nAuto queries the OS route to 1.1.1.1 / IPv6 without sending traffic; not all Internet usage.");
        if (snapshot.InterfaceCoverageLimited)
            _details.Text += T("\n系统接口超过128个；保留系统路由优先的128个。列表外手动接口可能受此上限影响。", "\nOS returned more than 128 interfaces; only 128 are retained, with the system route first. A missing manual interface may be excluded by this limit.");
        if (snapshot.State is "waiting" or "stalled")
            _details.Text += T("\n同步系统调用仍在等待，不会另开并发采集。调用返回后自动恢复；若一直不返回，可退出并重启应用。", "\nOne synchronous OS call is pending; no parallel replacement is started. Collection resumes when it returns. Restart the app if it never returns.");
        Chart.Apply(selected, now, snapshot.Version, fresh);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _source.Changed -= SourceChanged; _freshness.Dispose(); }
        base.Dispose(disposing);
    }
}
