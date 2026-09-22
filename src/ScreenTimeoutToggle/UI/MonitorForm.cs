using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using OBDim.Models;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI.Memory;
using OBDim.UI.Network;
using static OBDim.UI.UiPalette;

namespace OBDim.UI;

/// <summary>
/// Reusable tray popover for quota and memory. Dismissal hides the window; app exit
/// disposes it. SettingsRequested opens a separate persistent settings window.
/// Hidden pages record dirty state without creating or updating metric controls.
/// </summary>
public sealed class MonitorForm : Form
{
    private readonly MonitoringCoordinator _coordinator;

    private readonly ToolTip _toolTip = new();

    public enum View { Quota, Memory, Network }

    public event Action? SettingsRequested;
    public event Action<ProviderId>? ProviderSettingsRequested;

    private View _currentView = (View)(-1);

    // Header controls
    private readonly Panel _header = new();
    private readonly Button _quotaSegment = new();
    private readonly Button _memorySegment = new();
    private readonly Button _networkSegment = new();
    private readonly NetworkPage _networkView;
    private readonly Panel _quotaUnderline = new();
    private readonly Button _refreshButton = new();
    private readonly Button _settingsButton = new();

    // Views
    private readonly FlowLayoutPanel _quotaView = new() { AccessibleName = "Quota sources" };
    private readonly Label _quotaHint = new();
    private readonly Panel _memoryView = new();

    // Quota cards (one per provider)
    private readonly Dictionary<ProviderId, Panel> _cards = [];
    private readonly Dictionary<ProviderId, PictureBox> _cardLogos = [];
    private readonly Dictionary<ProviderId, Label> _cardTitles = [];
    private readonly Dictionary<ProviderId, BadgeLabel> _cardBadges = [];
    private readonly Dictionary<ProviderId, Label> _cardUpdated = [];
    private readonly Dictionary<ProviderId, Label> _cardStates = [];
    private readonly Dictionary<ProviderId, FlowLayoutPanel> _cardRows = [];

    /// <summary>Scales a reference-logical (96 DPI) length into this window's device pixels.
    /// Runtime-built row controls do not go through WinForms auto-scaling, so every fixed
    /// length they use must be scaled here; ctor-built controls use logical values and let
    /// AutoScaleMode.Dpi do the scaling.</summary>
    private int S(int logical) => (int)Math.Round(logical * (DeviceDpi / 96.0));

    // Incremental card updates: the rendered row STATE (signature) decides between an
    // in-place text/brush refresh (cheap, no control churn) and a structural rebuild.
    // Rebuilding dozens of controls per event was what made every interaction stall.
    private sealed record RowDesc(
        string Title, string Percent, double? Fraction, Color? BarColor,
        string Right, bool Dimmed, bool Clickable = false,
        string Badge = "", Color? ValueColor = null,
        bool SectionHeader = false, bool SeparatorAbove = false, string Identity = "");

    private readonly Dictionary<ProviderId, List<Control>> _cardRowControls = [];

    /// <summary>Provider logos shared by all card instances; loaded once, kept for the process lifetime.</summary>
    private static readonly Dictionary<ProviderId, Image> LogoCache = [];

    private static Image ProviderLogo(ProviderId id)
    {
        if (LogoCache.TryGetValue(id, out var cached)) return cached;
        var name = id switch
        {
            ProviderId.Codex => "provider-openai.png",
            ProviderId.MiniMax => "provider-minimax.png",
            _ => "provider-ark.png",
        };
        // GDI+ may lazily decode, so the stream must outlive the Image — copy the
        // embedded resource into a MemoryStream we deliberately never close.
        using var resource = typeof(MonitorForm).Assembly.GetManifestResourceStream($"OBDim.assets.{name}")
            ?? throw new InvalidOperationException($"Missing embedded logo resource: {name}");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var image = Image.FromStream(new MemoryStream(buffer.ToArray()));
        LogoCache[id] = image;
        return image;
    }

    // Memory view controls. The page is one chart plus a fixed six-field table: switching metric
    // changes only the series, so the figures that are always true stay on screen either way.
    private readonly Label _memoryStateLabel = new();
    private readonly Label _memorySampledLabel = new();
    private readonly Label _memoryHeadline = new();
    private readonly Label _memoryHeadlineNote = new();
    private readonly Label[,] _memoryStats = new Label[3, 2];
    private readonly MemoryTrendPanel _trendChart = new();
    private readonly Button _metricPhysicalButton = new();
    private readonly Button _metricCommitButton = new();
    private readonly Button[] _rangeButtons = [new(), new(), new(), new(), new()];
    private readonly Button _taskManagerButton = new();

    private static readonly MemoryTrendRange[] RangeOrder =
    [
        MemoryTrendRange.Minute1, MemoryTrendRange.Minute10, MemoryTrendRange.Minute30,
        MemoryTrendRange.Hour1, MemoryTrendRange.Hour2,
    ];

    private readonly Font _headlineFont = new("Microsoft YaHei UI", 21F, FontStyle.Bold);
    private readonly Font _segmentFont = new("Microsoft YaHei UI", 8.5F);

    private readonly Font _tabRegularFont = new("Microsoft YaHei UI", 13.5F);
    private readonly Font _tabBoldFont = new("Microsoft YaHei UI", 13.5F, FontStyle.Bold);
    private readonly Font _rowHeadingFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private readonly Font _timeFont = new("Microsoft YaHei UI", 8F);
    private readonly Font _badgeFont = new("Microsoft YaHei UI", 7.5F);
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 30000 };

    // Palette lives in UiPalette and arrives unqualified via `using static`, so the memory
    // page's extracted controls and this form cannot drift apart.

    public MonitorForm(MonitoringCoordinator coordinator)
    {
        _coordinator = coordinator;
        _networkView = new NetworkPage(coordinator.Network, enabled =>
        {
            var settings = coordinator.Settings with { NetworkEnabled = enabled };
            return coordinator.ApplyAndSaveSettingsAsync(settings);
        }) { Dock = DockStyle.Fill };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BackColor = PageBack;
        Font = new Font("Microsoft YaHei UI", 9F);
        // Reference content panel: 540×760 logical units (windows-quota-reference.html).
        // ShowAnchoredToTray clamps the height to the monitor's working area, so small
        // 150%-scaled screens scroll instead of spilling past the taskbar.
        ClientSize = new Size(540, 760);

        BuildHeader();
        BuildQuotaView();
        BuildMemoryView();
        _quotaView.SizeChanged += (_, _) => OnQuotaChanged();

        // Dock priority: the fill views must sit EARLIER in the collection than the Top
        // header, so the header reserves its strip first and the views take the rest.
        Controls.Add(_quotaView);
        Controls.Add(_memoryView);
        Controls.Add(_networkView);
        Controls.Add(_header);

        _coordinator.QuotaStateChanged += OnQuotaChanged;
        _coordinator.MemoryStateChanged += OnMemoryChanged;
        _coordinator.SettingsApplied += OnSettingsChanged;
        _countdownTimer.Tick += (_, _) => { if (_currentView == View.Quota) OnQuotaChanged(); };
        ApplyLocalization();

        // The three docked views stack; without this the LAST one (settings) renders on
        // top and the popover opens on the wrong page.
        SetView(View.Quota);
    }

    private static string L(string key, params object[] args) => LocalizationService.Get(key, args);

    public View CurrentView => _currentView;

    // ---------- chrome ----------

    /// <summary>Drop shadow for the borderless window (classic CS_DROPSHADOW).</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryEnableRoundedCorners();
    }

    /// <summary>Windows 11 rounded corners; gracefully does nothing on older systems.</summary>
    private void TryEnableRoundedCorners()
    {
        try
        {
            const int dwmwaWindowCornerPreference = 33;
            const int dwmwcpRounded = 2;
            var preference = dwmwcpRounded;
            var result = DwmSetWindowAttribute(Handle, dwmwaWindowCornerPreference, ref preference, sizeof(int));
            if (result < 0) Debug.WriteLine($"Window corners unavailable: HRESULT 0x{result:X8}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // pre-Win11 without dwmapi export — square corners are fine.
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _currentView == View.Network && _networkView.HandleEscape())
        { e.Handled = e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Escape && !ChartOwnsKey(e.KeyCode))
        {
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// When the panel was closed because it lost activation (the popover dismissal path).
    /// A tray click that steals focus closes the panel through this route and then arrives
    /// as a MouseClick — the toggle needs the timestamp to suppress the immediate reopen.
    /// </summary>
    internal DateTimeOffset? LastDeactivateClosedAtUtc { get; private set; }

    /// <summary>Popover behaviour: any loss of activation (click outside) dismisses the panel.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible)
        {
            LastDeactivateClosedAtUtc = DateTimeOffset.UtcNow;
            Hide();
        }
    }

    /// <summary>
    /// Ctrl+Tab cycles quota/memory/network; 1/2/4 jump to them and 3 opens settings. Navigation is used only when the user is
    /// not typing into a text/numeric field on the settings page, and never over the chart's
    /// own arrow keys: there Left/Right walk the sampled points, which is the keyboard path to
    /// a reading.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _currentView == View.Network && _networkView.HandleEscape()) return true;
        if (!IsTextInputActive() && !ChartOwnsKey(keyData))
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Tab:
                    SetView((View)(((int)_currentView + 1) % 3));
                    return true;
                case Keys.Right or Keys.Left:
                    SetView((View)(((int)_currentView + (keyData == Keys.Left ? 2 : 1)) % 3));
                    return true;
                case Keys.D1 or Keys.NumPad1:
                    SetView(View.Quota);
                    return true;
                case Keys.D2 or Keys.NumPad2:
                    SetView(View.Memory);
                    return true;
                case Keys.D4 or Keys.NumPad4:
                    SetView(View.Network);
                    return true;
                case Keys.D3 or Keys.NumPad3:
                    SettingsRequested?.Invoke();
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool IsTextInputActive()
    {
        Control? leaf = ActiveControl;
        while (leaf is ContainerControl container && container.ActiveControl is { } child) leaf = child;
        // Panels need not be ContainerControls. Follow ContainsFocus to the leaf.
        while (leaf?.Controls.Cast<Control>().FirstOrDefault(c => c.ContainsFocus) is { } child) leaf = child;
        return leaf is TextBoxBase or NumericUpDown or ComboBox;
    }

    /// <summary>
    /// True when the chart has focus and the key belongs to it. Escape is shared: the first press
    /// retires the chart's reading, and only once nothing is showing does it dismiss the popover.
    /// </summary>
    internal bool ChartOwnsKey(Keys keyData) => _currentView == View.Network && _networkView.OwnsKey(keyData) || keyData switch
    {
        Keys.Left or Keys.Right or Keys.Home or Keys.End => ActiveControl is MemoryTrendPanel,
        Keys.Escape => ActiveControl is MemoryTrendPanel { HasReading: true },
        _ => false,
    };

    /// <summary>
    /// Positions the popover against its tray icon (Shell_NotifyIconGetRect via the
    /// NotifyIcon's internal id/window — reflection with a cursor-based fallback) and
    /// shows it. Re-anchoring happens on every open, so a moved taskbar is always correct.
    /// </summary>
    public void ShowAnchoredToTray(NotifyIcon trayIcon)
    {
        var anchor = TryGetTrayIconRect(trayIcon, out var iconRect)
            ? ComputeAnchorFromIcon(iconRect)
            : ComputeAnchorFromCursor();
        Location = anchor;
        TopMost = true;
        Show();
        Activate();

        RefreshVisibleView();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        Volatile.Write(ref _visibleView, Visible ? (int)_currentView : -1);
        if (Visible)
        {
            Interlocked.Exchange(ref _quotaDirty, 1);
            Interlocked.Exchange(ref _memoryDirty, 1);
            RefreshVisibleView(); _countdownTimer.Start();
        }
        else _countdownTimer.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.QuotaStateChanged -= OnQuotaChanged;
            _coordinator.MemoryStateChanged -= OnMemoryChanged;
            _coordinator.SettingsApplied -= OnSettingsChanged;
            _countdownTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _tabRegularFont.Dispose(); _tabBoldFont.Dispose(); _rowHeadingFont.Dispose();
            _timeFont.Dispose(); _badgeFont.Dispose();
        }
    }

    private Point ComputeAnchorFromIcon(Rect iconRect)
    {
        var screen = Screen.FromRectangle(new Rectangle(iconRect.Left, iconRect.Top,
            Math.Max(1, iconRect.Right - iconRect.Left), Math.Max(1, iconRect.Bottom - iconRect.Top)));
        var work = screen.WorkingArea;
        ClampHeightToWorkArea(work);
        const int gap = 4;

        int x, y;
        if (iconRect.Top >= work.Bottom - 8)
        {
            // taskbar at the bottom → popover floats above the icon, right edges aligned
            x = iconRect.Right - Width;
            y = iconRect.Top - Height - gap;
        }
        else if (iconRect.Bottom <= work.Top + 8)
        {
            // taskbar at the top → popover hangs below the icon
            x = iconRect.Right - Width;
            y = iconRect.Bottom + gap;
        }
        else if (iconRect.Left >= work.Right - 8)
        {
            // taskbar on the right → popover to the left of the icon
            x = iconRect.Left - Width - gap;
            y = iconRect.Top;
        }
        else
        {
            // taskbar on the left → popover to the right of the icon
            x = iconRect.Right + gap;
            y = iconRect.Top;
        }

        return ClampToWorkArea(x, y, work);
    }

    private Point ComputeAnchorFromCursor()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var work = screen.WorkingArea;
        ClampHeightToWorkArea(work);
        var x = Cursor.Position.X + 12 - Width;
        var y = work.Bottom - Height - 8;
        return ClampToWorkArea(x, y, work);
    }

    /// <summary>
    /// The 760-logical panel must never spill past the taskbar on small or heavily
    /// scaled screens — shrink to the working area and let the content scroll.
    /// </summary>
    private void ClampHeightToWorkArea(Rectangle work)
    {
        const int margin = 8;
        var maxHeight = work.Height - 2 * margin;
        if (Height > maxHeight && maxHeight > 200) Height = maxHeight;
    }

    private Point ClampToWorkArea(int x, int y, Rectangle work)
    {
        const int margin = 8;
        return new Point(
            Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - Width - margin)),
            Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - Height - margin)));
    }

    private static bool TryGetTrayIconRect(NotifyIcon trayIcon, out Rect rect)
    {
        rect = default;
        try
        {
            // NotifyIcon keeps its Shell_NotifyIcon id and message window privately; the
            // shell can hand back the on-screen rect through them. Any drift in internals
            // degrades to the cursor fallback — never a crash.
            var idField = typeof(NotifyIcon).GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);
            var windowField = typeof(NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
            if (idField?.GetValue(trayIcon) is not int id) return false;
            if (windowField?.GetValue(trayIcon) is not NativeWindow window || window.Handle == IntPtr.Zero) return false;

            var identifier = new NotifyIconIdentifier
            {
                CbSize = Marshal.SizeOf<NotifyIconIdentifier>(),
                HWnd = window.Handle,
                UId = (uint)id,
                GuidItem = Guid.Empty,
            };
            return ShellNotifyIconGetRect(ref identifier, out rect) == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- header ----------

    private void BuildHeader()
    {
        // Reference: 56-logical-tall header, text tabs with a 3px accent underline, tool
        // icons on the right, hairline bottom border (windows-quota-reference.html .header).
        _header.Size = new Size(540, 56);
        _header.Dock = DockStyle.Top;
        _header.BackColor = PageBack;
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Hairline);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };

        StyleTabButton(_quotaSegment);
        _quotaSegment.Click += (_, _) => SetView(View.Quota);

        StyleTabButton(_memorySegment);
        _memorySegment.Click += (_, _) => SetView(View.Memory);
        StyleTabButton(_networkSegment);
        _networkSegment.Click += (_, _) => SetView(View.Network);

        _quotaUnderline.BackColor = Accent;
        _quotaUnderline.Visible = false;

        _refreshButton.AutoSize = false;
        _refreshButton.Size = new Size(32, 32);
        _refreshButton.Location = new Point(540 - 19 - 32 - 8 - 32, 12);
        _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshButton.FlatStyle = FlatStyle.Flat;
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 243, 244);
        _refreshButton.BackColor = PageBack;
        _refreshButton.Font = new Font("Segoe UI Symbol", 12F);
        _refreshButton.Cursor = Cursors.Hand;
        _refreshButton.Click += (_, _) => RequestRefreshForVisibleView();
        _refreshButton.AccessibleName = L("monitor.refresh_all");
        _toolTip.SetToolTip(_refreshButton, L("monitor.refresh_all"));

        _settingsButton.AutoSize = false;
        _settingsButton.Size = new Size(32, 32);
        _settingsButton.Location = new Point(540 - 19 - 32, 12);
        _settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsButton.FlatStyle = FlatStyle.Flat;
        _settingsButton.FlatAppearance.BorderSize = 0;
        _settingsButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 243, 244);
        _settingsButton.BackColor = PageBack;
        _settingsButton.Font = new Font("Segoe UI Symbol", 11F);
        _settingsButton.Cursor = Cursors.Hand;
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        _settingsButton.AccessibleName = L("monitor.settings");
        _toolTip.SetToolTip(_settingsButton, L("monitor.settings"));

        _header.Controls.Add(_quotaUnderline);
        _header.Controls.Add(_quotaSegment);
        _header.Controls.Add(_memorySegment);
        _header.Controls.Add(_networkSegment);
        _header.Controls.Add(_refreshButton);
        _header.Controls.Add(_settingsButton);
    }

    private void StyleTabButton(Button tab)
    {
        tab.AutoSize = false;
        tab.Size = new Size(56, 30);
        tab.FlatStyle = FlatStyle.Flat;
        tab.FlatAppearance.BorderSize = 0;
        tab.BackColor = PageBack;
        tab.Cursor = Cursors.Hand;
        tab.TextAlign = ContentAlignment.MiddleCenter;
    }

    public void SetView(View view)
    {
        if (_currentView == view) return;
        _currentView = view;
        if (view == View.Quota) Interlocked.Exchange(ref _quotaDirty, 1);
        _quotaView.Visible = view == View.Quota;
        _memoryView.Visible = view == View.Memory;
        _networkView.Visible = view == View.Network;
        _networkSegment.Font = view == View.Network ? _tabBoldFont : _tabRegularFont;
        _networkSegment.ForeColor = view == View.Network ? TextStrong : TextSecondary;
        _quotaSegment.Font = view == View.Quota ? _tabBoldFont : _tabRegularFont;
        _memorySegment.Font = view == View.Memory ? _tabBoldFont : _tabRegularFont;
        _quotaSegment.ForeColor = view == View.Quota ? TextStrong : TextSecondary;
        _memorySegment.ForeColor = view == View.Memory ? TextStrong : TextSecondary;
        LayoutTabs();
        Volatile.Write(ref _visibleView, Visible ? (int)view : -1);
        RefreshVisibleView();
        ApplyRefreshAffordance();
    }

    /// <summary>
    /// Top-bar refresh means "refresh what you are looking at". The memory page needs one system
    /// read; the quota page needs the provider CLIs. Routing both through RequestManualRefreshAll
    /// meant the memory page shell-spawned up to three CLIs to redraw a chart it could sample
    /// locally, and the quota page's "仅手动" promise was quietly broken by a memory click.
    /// </summary>
    private void RequestRefreshForVisibleView()
    {
        if (_currentView == View.Network) { _coordinator.Network.Refresh(); return; }
        if (_currentView == View.Memory)
        {
            _coordinator.RequestMemoryRefresh();
            ApplyRefreshAffordance();
            return;
        }

        _coordinator.RequestManualRefreshAll();
    }

    /// <summary>
    /// Keeps the refresh button's meaning, enabled state and accessible name in step with the
    /// visible page. While a memory read is running the button is disabled rather than queued —
    /// a second click cannot ask for a second sample, and the chart keeps showing the last one.
    /// </summary>
    private void ApplyRefreshAffordance()
    {
        if (IsDisposed) return;

        if (_currentView == View.Network)
        {
            _refreshButton.Enabled = _coordinator.Network.IsRunning;
            var networkText = LocalizationService.CurrentLanguage == "en-US" ? "Refresh network" : "刷新网络";
            _refreshButton.AccessibleName = networkText; _toolTip.SetToolTip(_refreshButton, networkText); return;
        }
        var onMemoryPage = _currentView == View.Memory;
        var text = onMemoryPage ? L("monitor.refresh_memory") : L("monitor.refresh_all");
        if (!_coordinator.Settings.MemoryEnabled) text = onMemoryPage ? L("monitor.refresh_memory_off") : text;

        _refreshButton.Enabled = !onMemoryPage
            || (_coordinator.Settings.MemoryEnabled && !_coordinator.MemoryRefreshInFlight);
        _refreshButton.AccessibleName = text;
        _toolTip.SetToolTip(_refreshButton, text);
    }

    /// <summary>Positions the two text tabs and the accent underline from the CURRENT text
    /// metrics — tab labels localize, so widths come from measurement, never constants.</summary>
    private void LayoutTabs()
    {
        var tabHeight = S(30);
        var y = (_header.Height - tabHeight) / 2;
        // +12: a Button's internal text area is ~8px narrower than its bounds, and GDI
        // WordBreak wraps CJK text between any two characters — with only +6 the second
        // character of 额度/内存 silently moved to a clipped second line (single-char tabs).
        var quotaWidth = TextRenderer.MeasureText(_quotaSegment.Text, _quotaSegment.Font).Width + S(12);
        var memoryWidth = TextRenderer.MeasureText(_memorySegment.Text, _memorySegment.Font).Width + S(12);
        _quotaSegment.SetBounds(S(19), y, quotaWidth, tabHeight);
        _memorySegment.SetBounds(S(19) + quotaWidth + S(24), y, memoryWidth, tabHeight);

        var networkWidth = TextRenderer.MeasureText(_networkSegment.Text, _networkSegment.Font).Width + S(12);
        _networkSegment.SetBounds(_memorySegment.Right + S(16), y, networkWidth, tabHeight);
        var selected = _currentView switch { View.Memory => _memorySegment, View.Network => _networkSegment, _ => _quotaSegment };
        _quotaUnderline.SetBounds(selected.Left, _header.Height - S(5), selected.Width, S(3));
        _quotaUnderline.Visible = true;
    }

    private static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------- quota view ----------

    private void BuildQuotaView()
    {
        _quotaView.Dock = DockStyle.Fill;
        _quotaView.FlowDirection = FlowDirection.TopDown;
        _quotaView.WrapContents = false;
        _quotaView.AutoScroll = true;
        _quotaView.Padding = new Padding(16);

        // With every source switched off the page would otherwise be blank with no clue why.
        // One line is cheaper than one card per provider.
        _quotaHint.AutoSize = true;
        _quotaHint.ForeColor = TextSecondary;
        _quotaHint.Margin = new Padding(0, 0, 0, 12);
        _quotaHint.Name = "quotaAllDisabledHint";
        _quotaView.Controls.Add(_quotaHint);

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var card = new CardPanel
            {
                Width = 508,
                BackColor = Color.White,
                AccessibleName = id.ToString() + " quota card",
                Margin = new Padding(0, 0, 0, 12),
                HeaderRuleY = 51,
            };

            // Provider header, one row like the reference: logo + name + tier badge +
            // "更新于 HH:mm", the hairline rule drawn by the card at HeaderRuleY.
            var logo = new PictureBox
            {
                Size = new Size(28, 28),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(13, 12),
                Image = ProviderLogo(id),
            };
            var title = new Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
                ForeColor = TextStrong,
                BackColor = Color.White,
                Location = new Point(51, 16),
                Text = L($"monitor.provider.{id.ToString().ToLowerInvariant()}"),
            };
            var badge = new BadgeLabel
            {
                Location = new Point(160, 16),
                Font = new Font("Microsoft YaHei UI", 8F),
            };
            var updated = new Label
            {
                AutoSize = true,
                ForeColor = TextSecondary,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8F),
                Location = new Point(220, 19),
            };
            var status = new LinkLabel
            {
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = TextSecondary,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8F),
                Location = new Point(13, 58),
            };
            var rows = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Location = new Point(0, 51),
                Width = 508,
                Margin = new Padding(0),
                BackColor = Color.White,
            };

            card.Controls.Add(logo);
            card.Controls.Add(title);
            card.Controls.Add(badge);
            card.Controls.Add(updated);
            status.Name=$"providerStatus_{id}";
            status.LinkBehavior=LinkBehavior.HoverUnderline;
            status.LinkClicked += (_,_) =>
            {
                var failure=_coordinator.GetDisplayState(id).LastAttempt;
                if (failure is null) return;
                if (ProviderFailureClassifier.IsAuthenticationFailure(failure.Error)
                    || failure.Error is ProviderErrorKind.CliNotFound or ProviderErrorKind.UnsupportedEntry or ProviderErrorKind.CliVersionUnsupported)
                    ProviderSettingsRequested?.Invoke(id);
                else _coordinator.RequestManualRefresh(id);
            };
            card.Controls.Add(status);
            card.Controls.Add(rows);

            _cards[id] = card;
            _cardLogos[id] = logo;
            _cardTitles[id] = title;
            _cardBadges[id] = badge;
            _cardUpdated[id] = updated;
            _cardStates[id] = status;
            _cardRows[id] = rows;
            _quotaView.Controls.Add(card);
        }
    }

    private int _pendingUiRefresh;
    private int _quotaDirty = 1;
    private int _memoryDirty = 1;
    private int _localizationDirty;
    private int _visibleView = -1;

    private void OnQuotaChanged()
    {
        Interlocked.Exchange(ref _quotaDirty, 1);
        if (Volatile.Read(ref _visibleView) == (int)View.Quota) QueueUiRefresh();
    }

    private void OnMemoryChanged()
    {
        Interlocked.Exchange(ref _memoryDirty, 1);
        if (Volatile.Read(ref _visibleView) == (int)View.Memory) QueueUiRefresh();
    }

    private void OnSettingsChanged()
    {
        Interlocked.Exchange(ref _localizationDirty, 1);
        Interlocked.Exchange(ref _quotaDirty, 1);
        Interlocked.Exchange(ref _memoryDirty, 1);
        QueueUiRefresh();
    }

    private void QueueUiRefresh()
    {
        if (IsDisposed || !IsHandleCreated || Volatile.Read(ref _visibleView) < 0) return;
        if (Interlocked.Exchange(ref _pendingUiRefresh, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                // Release before reading dirty flags. An event during rendering posts
                // another pass, instead of being swallowed by the coalescing gate.
                Interlocked.Exchange(ref _pendingUiRefresh, 0);
                if (!IsDisposed) RefreshVisibleView();
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _pendingUiRefresh, 0); }
    }

    private void RefreshVisibleView()
    {
        if (!Visible || IsDisposed) return;
        if (Interlocked.Exchange(ref _localizationDirty, 0) != 0) ApplyLocalization();
        if (_currentView == View.Quota && Interlocked.Exchange(ref _quotaDirty, 0) != 0) UpdateQuotaView();
        if (_currentView == View.Memory && Interlocked.Exchange(ref _memoryDirty, 0) != 0) UpdateMemoryView();
        if (_currentView == View.Network) { _networkView.Render(); ApplyRefreshAffordance(); }
    }

    internal int QuotaRefreshCount { get; private set; }
    internal int RowsCreated { get; private set; }
    internal int RowsDisposed { get; private set; }

    private void UpdateQuotaView()
    {
        QuotaRefreshCount++;
        if (IsDisposed) return;
        var cardWidth = Math.Max(200, _quotaView.ClientSize.Width - _quotaView.Padding.Horizontal);
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            if (_cards.TryGetValue(id, out var card) && card.Width != cardWidth) card.Width = cardWidth;
        }
        foreach (var state in _coordinator.GetDisplayStates())
        {
            UpdateCard(state);
        }

        _quotaHint.Visible = _cards.Values.All(c => !c.Visible);
    }

    private void UpdateCard(ProviderDisplayState state)
    {
        var card = _cards[state.Provider];

        // A provider the user switched off has nothing to report, so it renders nothing: no card,
        // no measured status text, no row pass. Flow layout skips hidden controls, so the cards
        // that do carry data move up without leaving a gap.
        if (!state.Enabled)
        {
            card.Visible = false;
            ReleaseCardRows(state.Provider);
            return;
        }

        card.Visible = true;
        var title = _cardTitles[state.Provider];
        var badge = _cardBadges[state.Provider];
        var updated = _cardUpdated[state.Provider];
        var statusLabel = _cardStates[state.Provider];
        var rowsPanel = _cardRows[state.Provider];

        var snapshot = state.LastGood;
        var hasFailure = state.LastAttempt is { HasError: true };

        // Header row like the reference: name + tier badge + "更新于 HH:mm" on ONE line.
        // The persistent "· 有效" word is gone — the timestamp IS the freshness signal;
        // an in-flight refresh is appended so progress stays visible.
        var updatedText = snapshot?.SucceededAtUtc is { } s ? L("monitor.updated", FormatTimeShort(s)) : "";
        if (state.Refreshing)
        {
            updatedText = updatedText.Length == 0
                ? L("monitor.state_refreshing")
                : updatedText + " · " + L("monitor.state_refreshing");
        }
        updated.Text = updatedText;
        updated.Visible = updatedText.Length > 0;

        // Tier badge is PROVIDER-level only where the source means it that way: Codex's
        // plan tier and MiniMax's validated inference sit next to the provider name; the Ark tier
        // belongs to its product section header, not here.
        var tier = snapshot?.Buckets.FirstOrDefault(b => b.Tier is not null)?.Tier;
        badge.Text = state.Provider == ProviderId.MiniMax ? snapshot?.PlanTier ?? ""
            : state.Provider == ProviderId.Codex && tier is not null ? TierDisplay(tier) : "";
        var showBadge = badge.Text.Length > 0;
        badge.Visible = showBadge;
        badge.FitToText();
        _toolTip.SetToolTip(badge,snapshot?.PlanTierIsInferred==true
            ? (LocalizationService.CurrentLanguage=="en-US"?"Inferred from validated daily video entitlement (minimax-video-daily-v1).":"依据视频每日权益推断（minimax-video-daily-v1），不是接口直接返回的套餐名。") : "");

        // Status line BELOW the header rule, only for facts the user must know — failure,
        // staleness, paused, or nothing fetched yet. A healthy card shows no status line at
        // all, and a switched-off provider never reaches this code.
        var parts = new List<string>();
        if(state.Authenticating) parts.Add(ProviderStatusPresentation.Connection(state,LocalizationService.CurrentLanguage=="en-US").Detail);
        if (hasFailure)
        {
            var failure=ProviderStatusPresentation.Failure(state.LastAttempt!.Error,state.LastAttempt.ExitCode,LocalizationService.CurrentLanguage=="en-US");
            parts.Add(failure.Title+"\n"+failure.Detail);
            parts.Add(LocalizationService.CurrentLanguage=="en-US"?"Click here to resolve / retry.":"点击此处处理／重试。");
        }
        if (state.Stale) parts.Add(L("monitor.state_stale"));
        if (state.PausedUntilUserRetry) parts.Add(L("monitor.paused_short"));
        if (snapshot is null && !hasFailure) parts.Add(L("monitor.state_no_data_yet"));
        statusLabel.Text = string.Join(" · ", parts);
        var showStatus = parts.Count > 0;
        statusLabel.Visible = showStatus;
        statusLabel.ForeColor = hasFailure ? BarRed : state.Stale ? StaleText : TextSecondary;

        // Header geometry from measured widths — everything here is in current-DPI
        // device pixels, so runtime offsets go through S(). Layout decisions read the
        // LOCAL booleans, never Control.Visible: that getter returns the WHOLE ancestor
        // chain state and lies during construction / BeginInvoke timing.
        title.Left = S(51);
        badge.Left = title.Right + S(10);
        badge.Top = title.Top + (title.Height - badge.Height) / 2;
        updated.Left = showBadge ? badge.Right + S(10) : badge.Left;
        updated.Top = title.Top + (title.Height - updated.Height) / 2;
        var statusWidth=Math.Max(S(80),card.Width-2*S(13));
        var statusHeight=showStatus?Math.Max(S(18),TextRenderer.MeasureText(statusLabel.Text,statusLabel.Font,
            new Size(statusWidth,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height+S(4)):0;
        statusLabel.AutoEllipsis=false;
        statusLabel.SetBounds(S(13),S(51)+S(8),statusWidth,statusHeight);
        if(statusLabel is LinkLabel link) link.LinkArea=hasFailure?new LinkArea(0,statusLabel.Text.Length):new LinkArea(0,0);

        // Rows start below the rule (or below the status line) — never at a fixed
        // offset from a control that may or may not be visible.
        var rowsY = S(51) + (showStatus ? S(8) + statusHeight + S(4) : S(4));
        if (rowsPanel.Location != new Point(0, rowsY)) rowsPanel.Location = new Point(0, rowsY);
        if (rowsPanel.Width != card.Width) rowsPanel.Width = card.Width;

        var dimmed = state.Stale || hasFailure;

        // Build the row DESCRIPTORS first; the signature decides between an in-place
        // refresh and a structural rebuild (see RowDesc note).
        var rows = new List<RowDesc>();

        if (snapshot is null)
        {
            // Nothing to tabulate yet — "尚未获取数据" or the failure reason is already on
            // the status line.
        }
        else
        {
            foreach (var bucket in snapshot.Buckets)
            {
                if (!_coordinator.Settings.IsProductVisible(state.Provider, bucket.SourceKey)) continue;
                if (state.Provider == ProviderId.Ark)
                {
                    // Product level lives HERE — "Coding Plan [Pro]" as a full-width
                    // section strip, like the reference. Never next to the provider
                    // name, never flattened into a window row.
                    rows.Add(new RowDesc(ArkProductDisplay(bucket), "", null, null, "",
                        dimmed, Badge: bucket.Tier is { } productTier ? TierDisplay(productTier) : "",
                        SectionHeader: true, Identity: $"product:{bucket.SourceKey}"));
                }

                if(bucket.RetainedFailureKind!=ProviderErrorKind.None)
                    rows.Add(MetricRow(ProviderStatusPresentation.Failure(bucket.RetainedFailureKind,english:LocalizationService.CurrentLanguage=="en-US").Title,
                        LocalizationService.CurrentLanguage=="en-US"?"Previous data":"保留上次数据","",null,null,"",true) with { Identity=$"retained:{bucket.SourceKey}" });
                if (bucket.Error is not null)
                {
                    rows.Add(MetricRow(bucket.DisplayName ?? bucket.SourceKey, ProviderStatusPresentation.Failure(bucket.ErrorKind,english:LocalizationService.CurrentLanguage=="en-US").Title,
                        "", null, null, "", true) with { Identity = $"status:{bucket.SourceKey}" });
                    continue;
                }
                if (bucket.Subscribed == false)
                {
                    rows.Add(MetricRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.no_subscription"),
                        "", null, null, "", true) with { Identity = $"status:{bucket.SourceKey}" });
                    continue;
                }
                if (bucket.Windows.Count == 0)
                {
                    rows.Add(MetricRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.not_returned"),
                        "", null, null, "", true) with { Identity = $"status:{bucket.SourceKey}" });
                    continue;
                }

                foreach (var window in bucket.Windows)
                {
                    // Mac overview policy: weekly video entitlement is detail-only.
                    if (state.Provider == ProviderId.MiniMax && bucket.SourceKey == "video" && window.SourceKey == "weekly") continue;
                    // Row identity per provider, mirroring the reference semantics:
                    // Codex rows read "Codex [每周]" / "Codex Spark [5 小时]" (model name
                    // as the FULL title, window as the badge — no manual truncation, the
                    // name column ellipsizes only when it truly does not fit); MiniMax's
                    // primary model shows windows as titles (当前周期/每周), secondary
                    // models as "视频赠送 [当日]"; Ark rows read "短周期 [当前会话]" and
                    // plain "每周".
                    var (rowTitle, rowBadge) = state.Provider switch
                    {
                        ProviderId.Codex => (CodexModelDisplay(bucket), WindowTitle(window)),
                        ProviderId.MiniMax when IsMiniMaxSecondaryModel(bucket) =>
                            (MiniMaxModelDisplay(bucket), MiniMaxWindowBadge(window)),
                        ProviderId.MiniMax => (WindowTitle(window), ""),
                        ProviderId.Ark => ArkRowTitles(window),
                        _ => (WindowTitle(window), ""),
                    };

                    double? fraction = null;
                    Color? barColor = null;
                    var percentText = "";
                    Color? valueColor = null;

                    if (window.IsUnlimited)
                    {
                        percentText = L("monitor.unlimited");
                        valueColor = UnlimitedBlue; // "∞ 无限制" is blue and carries no bar
                    }
                    else if (!window.HasAnyQuotaField)
                    {
                        percentText = L("monitor.not_provided");
                    }
                    else if (window.PercentOutOfRange || window.UsedPercent is < 0 or > 100)
                    {
                        percentText = L("monitor.percent_out_of_range",
                            FormatPercent((window.UsedPercent ?? window.RemainingPercent) ?? 0));
                    }
                    else
                    {
                        // Bar fill follows the DISPLAYED direction (used for MiniMax/Ark,
                        // remaining for Codex); the COLOR always grades by remaining, which
                        // is the number the reminder thresholds speak.
                        var remaining = window.RemainingPercent ?? 0;
                        barColor = remaining switch
                        {
                            <= ReminderEvaluator.Level2RemainingPercent => BarRed,
                            <= ReminderEvaluator.Level1RemainingPercent => BarAmber,
                            _ => BarGreen,
                        };

                        if (window.DisplayAsUsed && window.UsedPercent is { } used)
                        {
                            percentText = L("monitor.percent_used_only", FormatPercent(used));
                            fraction = Math.Clamp(used / 100.0, 0, 1);
                        }
                        else if (window.RemainingPercent is { } rem)
                        {
                            percentText = L("monitor.percent_remaining", FormatPercent(rem));
                            fraction = Math.Clamp(rem / 100.0, 0, 1);
                        }
                    }

                    // MiniMax-style absolute counts replace the percent text when present
                    // ("已用 0 / 3 次") and drop the bar — a count is not a percent.
                    if (state.Provider == ProviderId.MiniMax &&
                        window.UsedText is not null && window.TotalText is not null && !window.IsUnlimited)
                    {
                        percentText = L("monitor.counts_used", window.UsedText, window.TotalText);
                        fraction = null;
                        barColor = null;
                    }

                    rows.Add(MetricRow(rowTitle, percentText, rowBadge, fraction, barColor,
                        ResetText(window), dimmed, valueColor) with { Identity = $"metric:{bucket.SourceKey}\0{window.SourceKey}" });
                }
            }

            if (snapshot.ResetCredits is { } credits)
            {
                // Reference interaction: "Full reset [重置权益] 可用 3 次 ›" — the NEXT expiry
                // is always visible on the right, per-credit expiry lines are COLLAPSED until
                // the row is clicked. Expanded, EVERY credit gets a line so the count and the
                // list always agree.
                var nextExpiry = credits.Details?
                    .Where(c => c.ExpiresAtUtc.HasValue)
                    .OrderBy(c => c.ExpiresAtUtc)
                    .FirstOrDefault();
                var chevron = _creditsExpanded ? " ⌄" : " ›";
                var creditsTitle = credits.Details is { Count: > 0 } ? credits.Details[0].Title : L("monitor.badge.reset_credit");
                rows.Add(new RowDesc(
                    creditsTitle,
                    L("monitor.reset_credits_count", credits.AvailableCount) + chevron,
                    null, null,
                    nextExpiry?.ExpiresAtUtc is { } exp ? $"{exp.ToLocalTime():M/d HH:mm} " + L("monitor.credit_expiry_suffix") : "—",
                    dimmed, Clickable: true,
                    Badge: creditsTitle == L("monitor.badge.reset_credit") ? "" : L("monitor.badge.reset_credit"), Identity: "credits"));

                if (credits.Details is null)
                {
                    if (_creditsExpanded) rows.Add(MetricRow(L("monitor.reset_credits_count_only"), "", "", null, null, "", dimmed));
                }
                else if (_creditsExpanded)
                {
                    foreach (var credit in credits.Details)
                    {
                        var line = credit.Title;
                        if (credit.ExpiresAtUtc is { } expires)
                        {
                            line += " · " + L("monitor.reset_credit_expires", expires.ToLocalTime().ToString("M/d HH:mm"));
                        }
                        if (credit.Status is { } status && status != "available")
                        {
                            line += $" [{status}]";
                        }
                        rows.Add(MetricRow("• " + line, "", "", null, null, "", dimmed));
                    }
                }
            }
        }

        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Identity.Length == 0) rows[i] = rows[i] with { Identity = $"detail:{i}" };

        // Separators: a metric row that directly follows another metric row gets the
        // 1px top hairline (reference .metric+.metric:before). Section headers and the
        // first row under the header rule stay clean.
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].SectionHeader) continue;
            var prevIsMetric = i > 0 && !rows[i - 1].SectionHeader;
            if (rows[i].SeparatorAbove != prevIsMetric)
            {
                rows[i] = rows[i] with { SeparatorAbove = prevIsMetric };
            }
        }

        ApplyCardRows(state.Provider, rowsPanel, rows);

        SizeCard(card, rowsPanel);
    }

    /// <summary>A normal quota row (the only kind the metric grid renders).</summary>
    private static RowDesc MetricRow(string title, string value, string badge,
        double? fraction, Color? barColor, string right, bool dimmed, Color? valueColor = null) =>
        new(title, value, fraction, barColor, right, dimmed, Badge: badge, ValueColor: valueColor);

    /// <summary>
    /// Applies the row descriptors: a STRUCTURAL rebuild only when the signature changed,
    /// otherwise an in-place text/brush refresh on the existing controls. This is what
    /// keeps clicks and refresh cycles from rebuilding dozens of controls each time.
    /// </summary>
    /// <summary>
    /// Hands back the row controls a card was holding. Hiding the card is not enough on its own:
    /// the controls, their fonts and their GDI handles would stay alive for a source the user
    /// switched off, which is the cost this change is meant to remove.
    /// </summary>
    private void ReleaseCardRows(ProviderId id)
    {
        if (!_cardRowControls.TryGetValue(id, out var held) || held.Count == 0) return;
        var rowsPanel = _cardRows[id];
        _quotaView.SuspendLayout();
        rowsPanel.SuspendLayout();
        try
        {
            foreach (var row in held)
            {
                rowsPanel.Controls.Remove(row);
                row.Dispose();
                RowsDisposed++;
            }
        }
        finally
        {
            rowsPanel.ResumeLayout(true);
            _quotaView.ResumeLayout(true);
        }
        _cardRowControls[id] = [];
    }

    private void ApplyCardRows(ProviderId id, FlowLayoutPanel rowsPanel, List<RowDesc> rows)
    {
        var existing = _cardRowControls.GetValueOrDefault(id) ?? [];
        var remaining = existing.ToList();
        var next = new List<Control>(rows.Count);
        _quotaView.SuspendLayout();
        rowsPanel.SuspendLayout();
        try
        {
            foreach (var desc in rows)
            {
                var row = remaining.FirstOrDefault(c => c.Tag is RowDesc old
                    && old.Identity == desc.Identity && old.SectionHeader == desc.SectionHeader
                    && old.Clickable == desc.Clickable);
                if (row is null)
                {
                    row = AddRow(rowsPanel, desc);
                    if (desc.Clickable) MakeClickable(row, ToggleCreditsExpansion);
                }
                else remaining.Remove(row);
                UpdateRowInPlace(row, desc);
                if (row.Width != rowsPanel.Width) row.Width = rowsPanel.Width;
                LayoutRow(row, desc);
                next.Add(row);
            }
            foreach (var obsolete in remaining) { rowsPanel.Controls.Remove(obsolete); obsolete.Dispose(); RowsDisposed++; }
            for (var i = 0; i < next.Count; i++)
                if (rowsPanel.Controls.GetChildIndex(next[i]) != i) rowsPanel.Controls.SetChildIndex(next[i], i);
            _cardRowControls[id] = next;
        }
        finally { rowsPanel.ResumeLayout(true); _quotaView.ResumeLayout(true); }
    }

    private void UpdateRowInPlace(Control row, RowDesc desc)
    {
        if (row.Tag is RowDesc old && old.SeparatorAbove != desc.SeparatorAbove) row.Invalidate();
        row.Tag = desc;
        foreach (Control child in row.Controls)
        {
            switch (child.Tag)
            {
                case "t":
                    child.Text = desc.Title;
                    child.ForeColor = desc.Dimmed ? TextSecondary : desc.SectionHeader ? TextStrong : TextPrimary;
                    break;
                case "p":
                    child.Text = desc.Percent;
                    child.ForeColor = desc.Dimmed ? TextSecondary : desc.ValueColor ?? TextPrimary;
                    child.Visible = desc.Percent.Length > 0;
                    break;
                case "r": child.Text = desc.Right; child.Visible = desc.Right.Length > 0; break;
                case "badge":
                    child.Text = desc.Badge;
                    child.ForeColor = desc.Dimmed ? TextSecondary : BadgeText;
                    child.Visible = desc.Badge.Length > 0;
                    break;
                case "b" when child is QuotaBar bar:
                    bar.Fraction = desc.Fraction ?? 0;
                    bar.FillColor = desc.Dimmed ? TextSecondary : desc.BarColor ?? BarGreen;
                    bar.Visible = desc.Fraction.HasValue && desc.BarColor.HasValue;
                    break;
            }
        }
    }

    /// <summary>Expands/collapses the reset-credit expiry details; survives refreshes within the session.</summary>
    internal bool CreditsExpanded
    {
        get => _creditsExpanded;
        set
        {
            _creditsExpanded = value;
            UpdateQuotaView();
        }
    }

    private bool _creditsExpanded;

    private static void MakeClickable(Control row, Action onClick)
    {
        row.Cursor = Cursors.Hand;
        row.Click += (_, _) => onClick();
        foreach (Control child in row.Controls)
        {
            child.Cursor = Cursors.Hand;
            child.Click += (_, _) => onClick();
        }
    }

    private void ToggleCreditsExpansion()
    {
        _creditsExpanded = !_creditsExpanded;
        UpdateQuotaView();
    }

    /// <summary>
    /// Window titles follow the DURATION when the source provides one (10080 min → 每周,
    /// 300 min → 5 小时) — the same mental model as the macOS reference — falling back to
    /// the source label or the localized source key.
    /// </summary>
    private static string WindowTitle(QuotaWindow window)
    {
        switch (window.WindowDurationMinutes)
        {
            case 10080: return LocalizationService.Get("monitor.window.weekly");
            case 43200: return LocalizationService.Get("monitor.window.monthly");
            case 1440: return LocalizationService.Get("monitor.window.daily");
            case 300: return LocalizationService.Get("monitor.window.5h");
        }
        var localized = LocalizeWindowKey(window.SourceKey, window.Label);
        return localized;
    }

    /// <summary>MiniMax's primary model ("general") IS the plan — its rows need no badge.</summary>
    private static bool IsMiniMaxSecondaryModel(QuotaBucket bucket) =>
        !string.Equals(bucket.SourceKey, "general", StringComparison.OrdinalIgnoreCase);

    /// <summary>Friendly display for known MiniMax models; unknown models pass through.</summary>
    private static string MiniMaxModelDisplay(QuotaBucket bucket)
    {
        var model = bucket.DisplayName ?? bucket.SourceKey;
        return model.ToLowerInvariant() switch
        {
            "video" => LocalizationService.Get("monitor.minimax.model.video"),
            _ => model,
        };
    }

    /// <summary>
    /// Codex rows carry the PRODUCT name like the reference ("Codex Spark"), not the raw
    /// model id: the "GPT-x.y-" generation prefix is dropped and dashes become spaces.
    /// Anything else passes through untouched.
    /// </summary>
    private static string CodexModelDisplay(QuotaBucket bucket)
    {
        var raw = bucket.DisplayName ?? bucket.SourceKey;
        if (raw.StartsWith("GPT-", StringComparison.OrdinalIgnoreCase))
        {
            var productStart = raw.IndexOf("Codex", StringComparison.OrdinalIgnoreCase);
            if (productStart > 0) return raw[productStart..].Replace('-', ' ');
        }
        return raw;
    }

    /// <summary>
    /// Badge tag for a MiniMax secondary-model row: the video gift's daily window reads
    /// 当日 (not the generic 每日) — the macOS reference's wording for this row. Sources
    /// that omit the duration fall back to the interval key, which for the video model
    /// is the daily allowance.
    /// </summary>
    private static string MiniMaxWindowBadge(QuotaWindow window) =>
        window.WindowDurationMinutes == 1440 ||
        (window.WindowDurationMinutes is null && window.SourceKey == "interval")
            ? LocalizationService.Get("monitor.window.day")
            : WindowTitle(window);

    /// <summary>
    /// Ark rows keep the reference pairing: the session window is 短周期 [当前会话],
    /// every other window shows as a plain title (每周) with no badge.
    /// </summary>
    private static (string Title, string Badge) ArkRowTitles(QuotaWindow window) =>
        window.SourceKey == "session"
            ? (LocalizationService.Get("monitor.window.session_short"),
               LocalizationService.Get("monitor.window.session_badge"))
            : (WindowTitle(window), "");

    /// <summary>
    /// Product display name for an Ark bucket: "coding-plan" → "Coding Plan" — word-wise
    /// capitalization of the source identifier, the same rule the macOS mapper applies.
    /// Already-human names (Chinese, mixed case) pass through effectively unchanged.
    /// </summary>
    private static string ArkProductDisplay(QuotaBucket bucket)
    {
        var raw = bucket.DisplayName ?? bucket.SourceKey;
        var words = raw.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? raw : string.Join(" ",
            words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Source tiers are lowercase tags ("pro"); the UI shows them capitalized.</summary>
    private static string TierDisplay(string tier) =>
        tier.Length == 0 ? tier : char.ToUpperInvariant(tier[0]) + tier[1..];

    /// <summary>
    /// One quota row on the reference metric grid: fixed NAME column, flexible VALUE
    /// column (text over a 7px bar), fixed TIME column, 12-logical gaps. Column widths
    /// are the reference ratios (144 / * / 106 over a 482-logical content width at
    /// 540), so they hold at every DPI and window width without absolute pixel math.
    /// </summary>
    private Control AddRow(FlowLayoutPanel rowsPanel, RowDesc desc)
    {
        RowsCreated++;
        var row = new Panel { Width = rowsPanel.Width, Margin = new Padding(0),
            BackColor = desc.SectionHeader ? SectionBack : Color.White, Tag = desc };
        row.Paint += (_, e) =>
        {
            if (row.Tag is RowDesc { SeparatorAbove: true })
            {
                using var pen = new Pen(Hairline);
                e.Graphics.DrawLine(pen, S(13), 0, row.Width - S(13), 0);
            }
        };
        row.SizeChanged += (_, _) => { if (row.Tag is RowDesc value) LayoutRow(row, value); };
        row.Controls.Add(new Label { Tag = "t", AutoEllipsis = true,
            Font = desc.SectionHeader ? _rowHeadingFont : Font, BackColor = row.BackColor });
        row.Controls.Add(new BadgeLabel { Tag = "badge", Font = _badgeFont, BackColor = row.BackColor });
        if (!desc.SectionHeader)
        {
            row.Controls.Add(new Label { Tag = "p", AutoEllipsis = true, BackColor = row.BackColor });
            row.Controls.Add(new Label { Tag = "r", AutoEllipsis = true, BackColor = row.BackColor,
                ForeColor = TextSecondary, TextAlign = ContentAlignment.MiddleRight, Font = _timeFont });
            row.Controls.Add(new QuotaBar { Tag = "b", BackColor = row.BackColor });
        }
        rowsPanel.Controls.Add(row);
        return row;
    }

    private void LayoutRow(Control row, RowDesc desc)
    {
        var title = row.Controls.Cast<Control>().First(c => Equals(c.Tag, "t"));
        var badge = (BadgeLabel)row.Controls.Cast<Control>().First(c => Equals(c.Tag, "badge"));
        var hasBadge = desc.Badge.Length > 0;
        var inset = S(13);
        var contentWidth = Math.Max(S(80), row.Width - 2 * inset);
        var hasBar = desc.Fraction.HasValue && desc.BarColor.HasValue;
        var height = S(desc.SectionHeader ? 36 : hasBar ? 54 : 40);
        if (row.Height != height) row.Height = height;
        badge.Height = S(18);
        var measured = TextRenderer.MeasureText(desc.Title, title.Font).Width + S(8);
        if (desc.SectionHeader)
        {
            title.SetBounds(inset, (height-S(20))/2,
                Math.Min(measured, Math.Max(S(20), contentWidth - (hasBadge ? badge.Width+S(8) : 0))), S(20));
            badge.Location = new Point(title.Right+S(8), (height-badge.Height)/2);
            return;
        }
        var nameWidth = (int)Math.Round(contentWidth * (144.0/482.0));
        var rightWidth = (int)Math.Round(contentWidth * (106.0/482.0));
        var fullLine = !hasBadge && desc.Percent.Length == 0 && desc.Right.Length == 0;
        var titleAvailable = fullLine ? contentWidth : Math.Max(S(20), nameWidth - (hasBadge ? badge.Width+S(6) : 0));
        title.SetBounds(inset, (height-S(20))/2, Math.Min(measured, titleAvailable), S(20));
        badge.Location = new Point(Math.Min(title.Right+S(6), inset+nameWidth-badge.Width), (height-badge.Height)/2);
        var valueX = inset + nameWidth + S(12);
        var valueWidth = Math.Max(S(40), contentWidth-nameWidth-rightWidth-2*S(12));
        foreach (Control child in row.Controls)
        {
            if (Equals(child.Tag, "p")) child.SetBounds(valueX, hasBar ? S(12) : (height-S(20))/2, valueWidth, S(20));
            if (Equals(child.Tag, "r")) child.SetBounds(row.Width-inset-rightWidth, (height-S(20))/2, rightWidth, S(20));
            if (Equals(child.Tag, "b")) child.SetBounds(valueX, S(35), valueWidth, S(7));
        }
    }

    private void SizeCard(Panel card, FlowLayoutPanel rowsPanel)
    {
        var height = rowsPanel.Top + rowsPanel.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + S(12);
        if (card.Height != height) card.Height = height;
    }

    private static string ResetText(QuotaWindow window)
    {
        if (window.ResetsAtUtc is null) return "—";
        var local = window.ResetsAtUtc.Value.ToLocalTime();
        var remaining = local - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return LocalizationService.Get("monitor.reset_pending");
        }
        // 像参照一样：一天内用倒计时，更远用绝对时间（无空格拼接，右列窄也能放下）。
        if (remaining <= TimeSpan.FromHours(24))
        {
            return FormatCountdown(remaining) + LocalizationService.Get("monitor.reset_in");
        }
        return $"{local:M/d HH:mm} " + LocalizationService.Get("monitor.reset_suffix");
    }

    private static string ErrorKeyFor(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.CliNotFound => "monitor.error.cli_not_installed",
        ProviderErrorKind.UnsupportedEntry => "monitor.error.unsupported_entry",
        ProviderErrorKind.NotSignedIn => "monitor.error.not_signed_in",
        ProviderErrorKind.ApiError => "monitor.error.api_error",
        ProviderErrorKind.Timeout => "monitor.error.timeout",
        ProviderErrorKind.OutputLimitExceeded => "monitor.error.output_limit",
        ProviderErrorKind.ParseFailed => "monitor.error.parse_failed",
        ProviderErrorKind.Cancelled => "monitor.error.cancelled",
        ProviderErrorKind.Disabled => "monitor.state_disabled",
        _ => "monitor.error.execution_failed",
    };

    // ---------- memory view ----------

    private void BuildMemoryView()
    {
        // TableLayoutPanel rows (AutoSize) instead of hand-computed pixel offsets — the
        // manual layout was unstable under DPI scaling (controls overlapped on a real
        // 150% machine).
        _memoryView.Dock = DockStyle.Fill;
        _memoryView.Padding = new Padding(12, 6, 12, 12);
        _memoryView.BackColor = PageBack;
        // A short work area scrolls vertically rather than squeezing the chart into a few
        // pixels; the task-manager entry stays reachable. Horizontal scrolling never applies.
        _memoryView.AutoScroll = true;

        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(14, 12, 14, 12),
            MinimumSize = new Size(0, S(432)),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Color.White,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _memoryStateLabel.AutoSize = true;
        _memoryStateLabel.Font = new Font(Font, FontStyle.Bold);
        _memoryStateLabel.Margin = new Padding(0, 0, 0, 8);

        // ---- headline row: the selected figure, and the physical/commit switch ----
        var headlineRow = NewTwoColumnRow();

        var headlineStack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            BackColor = Color.White,
            Dock = DockStyle.Left,
        };
        headlineStack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headlineStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headlineStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _memoryHeadline.AutoSize = true;
        _memoryHeadline.Font = _headlineFont;
        _memoryHeadline.ForeColor = TextStrong;
        _memoryHeadline.Margin = new Padding(0);
        _memoryHeadlineNote.AutoSize = true;
        _memoryHeadlineNote.Font = new Font(Font.FontFamily, 8.5F);
        _memoryHeadlineNote.ForeColor = TextSecondary;
        _memoryHeadlineNote.Margin = new Padding(0);
        headlineStack.Controls.Add(_memoryHeadline, 0, 0);
        headlineStack.Controls.Add(_memoryHeadlineNote, 0, 1);

        var switcher = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.White,
            Dock = DockStyle.Right,
            Margin = new Padding(0, 6, 0, 0),
        };
        StyleSegment(_metricPhysicalButton);
        StyleSegment(_metricCommitButton);
        _metricPhysicalButton.Font = _segmentFont;
        _metricCommitButton.Font = _segmentFont;
        _metricPhysicalButton.Click += (_, _) => _trendChart.Metric = MemoryTrendMetric.PhysicalUsed;
        _metricCommitButton.Click += (_, _) => _trendChart.Metric = MemoryTrendMetric.Commit;
        switcher.Controls.Add(_metricPhysicalButton);
        switcher.Controls.Add(_metricCommitButton);

        headlineRow.Controls.Add(headlineStack, 0, 0);
        headlineRow.Controls.Add(switcher, 1, 0);

        // ---- range row: five windows over the same stored history ----
        var rangeRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Top,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 4),
        };
        for (var i = 0; i < _rangeButtons.Length; i++)
        {
            var index = i;
            StyleSegment(_rangeButtons[index]);
            _rangeButtons[index].Font = _segmentFont;
            _rangeButtons[index].Click += (_, _) => _trendChart.Range = RangeOrder[index];
            rangeRow.Controls.Add(_rangeButtons[index]);
        }

        // ---- six-field table: left column physical, right column commit ----
        var statsGrid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 3,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 4),
        };
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                var valueLabel = new Label
                {
                    AutoSize = true,
                    Font = new Font(Font.FontFamily, 8.5F),
                    ForeColor = SystemColors.ControlText,
                    Margin = new Padding(0, 3, 4, 3),
                };
                _memoryStats[row, col] = valueLabel;
                statsGrid.Controls.Add(valueLabel, col, row);
            }
        }

        _memorySampledLabel.AutoSize = true;
        _memorySampledLabel.ForeColor = TextSecondary;
        _memorySampledLabel.Font = new Font(Font.FontFamily, 8F);
        _memorySampledLabel.Margin = new Padding(0, 0, 0, 6);

        _trendChart.Dock = DockStyle.Fill;
        _trendChart.Margin = new Padding(0, 0, 0, 8);
        _trendChart.MinimumSize = new Size(0, S(120));
        _trendChart.SelectionChanged += ApplyMemorySelection;

        _taskManagerButton.AutoSize = true;
        _taskManagerButton.FlatStyle = FlatStyle.Flat;
        _taskManagerButton.FlatAppearance.BorderSize = 0;
        _taskManagerButton.BackColor = Color.FromArgb(232, 232, 236);
        _taskManagerButton.Cursor = Cursors.Hand;
        _taskManagerButton.Margin = new Padding(0, 0, 0, 0);
        _taskManagerButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to start task manager", ex);
            }
        };

        var footerRow = NewTwoColumnRow();
        var footerRight = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.White,
            Dock = DockStyle.Right,
        };
        footerRight.Controls.Add(_taskManagerButton);
        footerRow.Controls.Add(_memorySampledLabel, 0, 0);
        footerRow.Controls.Add(footerRight, 1, 0);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_memoryStateLabel, 0, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(headlineRow, 0, 1);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(rangeRow, 0, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_trendChart, 0, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(statsGrid, 0, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(footerRow, 0, 5);

        card.Controls.Add(layout);
        _memoryView.Controls.Add(card);
    }

    /// <summary>A one-row, two-column container: a stretched left cell and a right-aligned one.</summary>
    private static TableLayoutPanel NewTwoColumnRow()
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 4),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return row;
    }

    /// <summary>
    /// Segmented-control look for the metric and range switches. Font is NOT touched here: this
    /// runs on every selection change, and allocating a Font per click is how GDI objects leak.
    /// </summary>
    private void StyleSegment(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = CardBorder;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = SectionBack;
        button.AutoSize = true;
        button.Padding = new Padding(S(7), S(2), S(7), S(2));
        button.Margin = new Padding(0);
        button.Cursor = Cursors.Hand;
        button.TabStop = true;
    }

    /// <summary>Reflects the chart's selection onto the two switch groups and the titles.</summary>
    private void ApplyMemorySelection()
    {
        if (IsDisposed) return;

        PaintSegment(_metricPhysicalButton, _trendChart.Metric == MemoryTrendMetric.PhysicalUsed);
        PaintSegment(_metricCommitButton, _trendChart.Metric == MemoryTrendMetric.Commit);
        for (var i = 0; i < _rangeButtons.Length; i++)
        {
            PaintSegment(_rangeButtons[i], _trendChart.Range == RangeOrder[i]);
        }

        _trendChart.Title = L("monitor.trend_title_range", RangeLabel(_trendChart.Range));
        _metricPhysicalButton.AccessibleName = L("monitor.metric_physical");
        _metricCommitButton.AccessibleName = L("monitor.metric_commit");
        UpdateMemoryHeadline();
    }

    private static void PaintSegment(Button button, bool selected)
    {
        button.BackColor = selected ? Accent : Color.White;
        button.ForeColor = selected ? Color.White : TextSecondary;
    }

    private static string RangeLabel(MemoryTrendRange range) => L(range switch
    {
        MemoryTrendRange.Minute1 => "monitor.range_1m",
        MemoryTrendRange.Minute10 => "monitor.range_10m",
        MemoryTrendRange.Minute30 => "monitor.range_30m",
        MemoryTrendRange.Hour1 => "monitor.range_1h",
        MemoryTrendRange.Hour2 => "monitor.range_2h",
        _ => "monitor.range_1h",
    });

    /// <summary>
    /// The headline is the selected metric's own figure and its own denominator, both taken from
    /// the same sample: 物理 uses used/total and 提交 uses committed/limit. Summing or stacking the
    /// two would imply they measure the same pool.
    /// </summary>
    private void UpdateMemoryHeadline()
    {
        if (IsDisposed) return;

        var point = _trendChart.CurrentPoint;
        if (point is null)
        {
            _memoryHeadline.Text = "—";
            _memoryHeadlineNote.Text = L("monitor.not_provided");
            _memoryHeadline.ForeColor = TextSecondary;
            return;
        }

        var percent = (double)point.Value.Value / point.Value.Capacity * 100.0;
        _memoryHeadline.Text = FormatBytes(point.Value.Value);
        _memoryHeadline.ForeColor = _trendChart.Stale ? BarAmber : TextStrong;
        _memoryHeadlineNote.Text = L(
            _trendChart.Metric == MemoryTrendMetric.PhysicalUsed ? "monitor.headline_physical" : "monitor.headline_commit",
            percent.ToString("0.#") + "%");
    }

    private void UpdateMemoryView()
    {
        if (IsDisposed) return;
        var settings = _coordinator.Settings;
        var sample = _coordinator.LatestMemorySample;
        var error = _coordinator.LastMemoryError;
        var stale = _coordinator.MemoryStale;

        if (!settings.MemoryEnabled)
        {
            _memoryStateLabel.Text = L("monitor.memory_disabled");
            _memoryStateLabel.ForeColor = SystemColors.ControlText;
        }
        else if (sample is null)
        {
            _memoryStateLabel.Text = error is null ? L("monitor.memory_no_data") : L("monitor.memory_error", error);
            _memoryStateLabel.ForeColor = error is null ? SystemColors.ControlText : BarRed;
        }
        else if (stale)
        {
            _memoryStateLabel.Text = L("monitor.memory_stale");
            _memoryStateLabel.ForeColor = BarAmber;
        }
        else
        {
            _memoryStateLabel.Text = L("monitor.memory_live");
            _memoryStateLabel.ForeColor = SystemColors.ControlText;
        }

        // Left column physical, right column commit. Nothing here is a "health score": an
        // un-triggered low-memory signal is one fact about one notification, not a verdict.
        if (sample is not null)
        {
            _memoryStats[0, 0].Text = $"{L("monitor.memory.stat_physical")}  {FormatBytes(sample.PhysicalTotalBytes)}";
            _memoryStats[1, 0].Text = $"{L("monitor.memory.stat_used")}  {FormatBytes(sample.PhysicalUsedBytes)}";
            _memoryStats[2, 0].Text = $"{L("monitor.memory.stat_available")}  {FormatBytes(sample.PhysicalAvailableBytes)}";
            _memoryStats[0, 1].Text = $"{L("monitor.memory.stat_commit")}  {FormatBytes(sample.CommitTotalBytes)}";
            _memoryStats[1, 1].Text = $"{L("monitor.memory.stat_commit_limit")}  {FormatBytes(sample.CommitLimitBytes)}";
            _memoryStats[2, 1].Text = $"{L("monitor.memory.stat_low_signal")}  {sample.LowMemorySignal switch
            {
                true => L("monitor.low_triggered"),
                false => L("monitor.low_not_triggered"),
                null => L("monitor.low_unknown"),
            }}";
            _memoryStats[2, 1].ForeColor = sample.LowMemorySignal == true ? BarAmber : SystemColors.ControlText;
            _memorySampledLabel.Text = L("monitor.memory_sampled", FormatTime(sample.SampledAtUtc));
        }
        else
        {
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    _memoryStats[row, col].Text = L("monitor.not_provided");
                    _memoryStats[row, col].ForeColor = SystemColors.ControlText;
                }
            }
            _memorySampledLabel.Text = L("monitor.memory_sampled", "—");
        }

        _trendChart.Apply(_coordinator.MemoryHistory.SnapshotWithVersion(), stale);
        UpdateMemoryHeadline();
        ApplyRefreshAffordance();
    }

    // ---------- settings view ----------

    // ---------- localization refresh ----------

    private void ApplyLocalization()
    {
        Text = L("monitor.title");
        _quotaSegment.Text = L("monitor.tab_quota");
        _memorySegment.Text = L("monitor.tab_memory");
        _networkSegment.Text = LocalizationService.CurrentLanguage == "en-US" ? "Network" : "网络";
        _refreshButton.Text = "⟳";
        _settingsButton.Text = "⚙";
        _quotaHint.Text = L("monitor.quota_all_disabled");
        _taskManagerButton.Text = L("monitor.open_task_manager");
        _metricPhysicalButton.Text = L("monitor.metric_physical");
        _metricCommitButton.Text = L("monitor.metric_commit");
        for (var i = 0; i < _rangeButtons.Length; i++)
        {
            _rangeButtons[i].Text = RangeLabel(RangeOrder[i]);
            _rangeButtons[i].AccessibleName = RangeLabel(RangeOrder[i]);
        }
        _trendChart.EmptyText = L("monitor.trend_no_data");
        _trendChart.NoSamplesText = L("monitor.trend_gap_no_samples");
        _trendChart.AccessibleName = L("monitor.tab_memory");
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (_cardTitles.TryGetValue(id, out var title))
            {
                title.Text = L($"monitor.provider.{id.ToString().ToLowerInvariant()}");
            }
        }
        LayoutTabs();
        ApplyMemorySelection();
        _trendChart.RebuildGeometry();
    }

    // ---------- statics shared with tests / tray ----------

    internal static string LocalizeWindowKey(string sourceKey, string? label)
    {
        var key = sourceKey switch
        {
            "primary" => "monitor.window.primary",
            "secondary" => "monitor.window.secondary",
            "interval" => "monitor.window.interval",
            "weekly" => "monitor.window.weekly",
            "monthly" => "monitor.window.monthly",
            "session" => "monitor.window.session",
            "5h" => "monitor.window.5h",
            "individualLimit" => "monitor.window.individual_limit",
            _ => null,
        };
        if (key is not null) return LocalizationService.Get(key);
        return label ?? sourceKey;
    }

    /// <summary>Integers render bare ("65"); fractions keep their real precision
    /// ("97.93") — trimming to one decimal was lossy against the reference.</summary>
    internal static string FormatPercent(double value) =>
        value == Math.Floor(value) ? ((int)value).ToString() : value.ToString("0.##");

    internal static string FormatBytes(ulong bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        const double mb = 1024.0 * 1024;
        return bytes >= (ulong)gb
            ? (bytes / gb).ToString("0.#") + " GB"
            : (bytes / mb).ToString("0") + " MB";
    }

    internal static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    internal static string FormatTimeShort(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm");

    internal static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
        {
            return LocalizationService.Get("common.countdown_days_hours",
                (int)remaining.TotalDays, remaining.Hours);
        }
        if (remaining.TotalHours >= 1)
        {
            return LocalizationService.Get("common.countdown_hours_minutes",
                (int)remaining.TotalHours, remaining.Minutes);
        }
        return LocalizationService.Get("common.countdown_minutes", Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
    }

    // ---------- shell interop for tray anchoring ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int CbSize;
        public IntPtr HWnd;
        public uint UId;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- small custom controls ----------

    /// <summary>
    /// Read-only capture box for a global hotkey: click, press a combination, done.
    /// Same validation rules as the classic SettingsForm (function keys may go bare).
    /// </summary>
    internal sealed class HotkeyCaptureBox : TextBox
    {
        public HotkeyConfig Captured { get; private set; } = new();

        public void SetCaptured(HotkeyConfig config)
        {
            Captured = config;
            Text = $"{config.Modifiers}+{config.Key}";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Even without focus styles, arrows/Tab must reach the capture logic.
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            var mods = new List<string>();
            if (e.Control) mods.Add("Ctrl");
            if (e.Alt) mods.Add("Alt");
            if (e.Shift) mods.Add("Shift");

            var key = e.KeyCode;
            if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey) return;

            var keyStr = key.ToString();
            if (HotkeyService.KeyStringToVk(keyStr) == 0) return; // ignore unbindable keys silently

            if (mods.Count == 0 && !IsFunctionKey(keyStr))
            {
                // Bare letter/digit keys clash with typing; keep the popover hotkey sane
                // by requiring a modifier unless it is a function key.
                return;
            }

            Captured = new HotkeyConfig { Modifiers = string.Join("+", mods), Key = keyStr };
            Text = $"{Captured.Modifiers}+{Captured.Key}";
        }

        private static bool IsFunctionKey(string key)
        {
            if (HotkeyService.TryParseFunctionKeyNumber(key.Trim().ToUpperInvariant(), out _)) return true;
            return key.Trim().ToUpperInvariant() switch
            {
                "PRINTSCREEN" or "SNAPSHOT" or "PAUSE" or "SCROLL" or "SCROLLLOCK"
                    or "CAPITAL" or "CAPSLOCK" or "NUMLOCK" => true,
                _ => false,
            };
        }
    }

    /// <summary>A 6px rounded quota bar — the only "chart" a quota row needs.</summary>
    internal sealed class QuotaBar : Control
    {
        private double _fraction;
        private Color _fillColor = BarGreen;
        public double Fraction
        {
            get => _fraction;
            set { if (_fraction == value) return; _fraction = value; Invalidate(); }
        }
        public Color FillColor
        {
            get => _fillColor;
            set { if (_fillColor == value) return; _fillColor = value; Invalidate(); }
        }

        public QuotaBar()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);
            // Reference: 7-logical-tall fully rounded track (#f0f1f2) and fill.
            using var trackBrush = new SolidBrush(TrackBack);
            using var trackPath = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
            g.FillPath(trackBrush, trackPath);

            var fillWidth = (int)Math.Round(Width * Math.Clamp(Fraction, 0, 1));
            // 0% draws NOTHING — a forced minimum-width stub would fake usage that is
            // not there (the reference keeps an empty track for Ark's 已用 0%).
            if (Fraction > 0 && fillWidth > 0)
            {
                using var fillBrush = new SolidBrush(FillColor);
                using var fillPath = RoundedPath(
                    new Rectangle(0, 0, Math.Max(fillWidth - 1, Height / 2), Height - 1), Height / 2);
                g.FillPath(fillBrush, fillPath);
            }
        }
    }

    /// <summary>Small rounded badge text (tier names like Pro, plan labels, window tags).
    /// Flat #e9e9eb fill with a 5-logical radius — not a pill — per the reference.</summary>
    internal sealed class BadgeLabel : Control
    {
        public BadgeLabel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            ForeColor = BadgeText;
            Size = new Size(36, 18);
        }

        /// <summary>
        /// Sizes the badge from the CURRENT font right now — the caller does this when the
        /// text changes, in the same coordinate space as the rest of the row. Sizing inside
        /// OnPaint turned out to be unreliable (the extra repaint is not guaranteed before
        /// a DrawToBitmap capture, and pre-handle measurements miss DPI scale).
        /// </summary>
        public void FitToText()
        {
            var textSize = TextRenderer.MeasureText(Text, Font);
            var padding = (int)Math.Round(14 * (DeviceDpi / 96.0));
            Width = Math.Max(20, textSize.Width + padding);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Text.Length == 0) return;

            var radius = (int)Math.Round(5 * (DeviceDpi / 96.0));
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var brush = new SolidBrush(BadgeBack);
            using var path = RoundedPath(rect, Math.Min(radius, rect.Height / 2));
            g.FillPath(brush, path);
            TextRenderer.DrawText(g, Text, Font, rect, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            FitToText();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitToText();
        }
    }

    /// <summary>White rounded card with a hairline border (provider sections / view pages).</summary>
    internal sealed class CardPanel : Panel
    {
        private GraphicsPath? _cachedPath;
        private Size _cachedSize;

        /// <summary>When set (logical 96-DPI units), a hairline is drawn under the quota
        /// card's provider header, inset 13 like the reference.</summary>
        public int? HeaderRuleY { get; set; }

        public CardPanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_cachedSize != Size || _cachedPath is null)
            {
                _cachedPath?.Dispose();
                var scale = DeviceDpi / 96.0;
                var radius = (int)Math.Round(12 * scale);
                _cachedPath = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), radius);
                _cachedSize = Size;
            }
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, _cachedPath);
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawPath(pen, _cachedPath);

            if (HeaderRuleY is { } ruleY)
            {
                var scale = DeviceDpi / 96.0;
                var inset = (int)Math.Round(13 * scale);
                var y = (int)Math.Round(ruleY * scale);
                using var rulePen = new Pen(Hairline);
                e.Graphics.DrawLine(rulePen, inset, y, Width - inset, y);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cachedPath?.Dispose();
            base.Dispose(disposing);
        }
    }

}
