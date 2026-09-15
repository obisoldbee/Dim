using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using OBDim.Models;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;

namespace OBDim.UI;

/// <summary>
/// The "Usage &amp; Memory" popover: a borderless panel anchored to its tray icon — the
/// macOS menu-bar-popover interaction the user asked for (2026-09-13 feedback). One
/// instance at a time; closes on Esc, on losing activation (click outside), or via the
/// tray menu / hotkey toggle. Closing the popover does NOT stop the coordinator —
/// monitoring keeps running in the background; only app exit does.
/// <para>
/// Three views inside one panel: 额度 (quota cards), 内存 (memory stats + trend), 设置 —
/// and the settings view holds the FULL app settings (screen-timeout modes, both hotkeys,
/// autostart, language) plus the monitoring switches, per the user's request to merge the
/// two settings pages. Keyboard: the global popover hotkey toggles the panel; Tab / ← / →
/// switch 额度↔内存; 1/2/3 jump directly; Esc closes. Text fields take the keys first.
/// </para>
/// </summary>
public sealed class MonitorForm : Form
{
    private readonly MonitoringCoordinator _coordinator;

    /// <summary>App-config access for the merged settings view. Null in tests (section hidden).</summary>
    private readonly Func<AppConfig>? _appConfigGetter;
    private readonly Func<AppConfig, bool>? _appConfigApplier;

    private readonly ToolTip _toolTip = new();

    public enum View { Quota, Memory, Settings }

    private View _currentView = View.Quota;

    // Header controls
    private readonly Panel _header = new();
    private readonly Button _quotaSegment = new();
    private readonly Button _memorySegment = new();
    private readonly Panel _quotaUnderline = new();
    private readonly Button _refreshButton = new();
    private readonly Button _settingsButton = new();

    // Views
    private readonly FlowLayoutPanel _quotaView = new();
    private readonly Panel _memoryView = new();
    private readonly Panel _settingsView = new();

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
        bool SectionHeader = false, bool SeparatorAbove = false);

    private readonly Dictionary<ProviderId, string> _cardSignatures = [];
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

    // Memory view controls
    private readonly Label _memoryStateLabel = new();
    private readonly Label _memorySampledLabel = new();
    private readonly Label[,] _memoryStats = new Label[3, 2];
    private readonly MemoryTrendChart _trendChart = new();
    private readonly Button _taskManagerButton = new();

    // Settings view controls
    private readonly Dictionary<ProviderId, CheckBox> _enableChecks = [];
    private readonly Dictionary<ProviderId, TextBox> _pathBoxes = [];
    private readonly CheckBox _memoryCheck = new();
    private readonly CheckBox _remindersCheck = new();
    private readonly CheckBox _leftClickCheck = new();
    private readonly HotkeyCaptureBox _switchHotkeyBox = new();
    private readonly HotkeyCaptureBox _popoverHotkeyBox = new();
    private readonly NumericUpDown _workAc = new();
    private readonly NumericUpDown _workDc = new();
    private readonly NumericUpDown _awayAc = new();
    private readonly NumericUpDown _awayDc = new();
    private readonly ComboBox _languageBox = new();
    private readonly Button _saveButton = new();
    private readonly Label _saveStatusLabel = new();

    // Palette lifted from the reference handoff (windows-quota-reference.html :root).
    private static readonly Color Accent = Color.FromArgb(0x14, 0x79, 0xFA);       // #1479fa
    private static readonly Color BarGreen = Color.FromArgb(0x2A, 0xBD, 0x51);     // #2abd51
    private static readonly Color BarAmber = Color.FromArgb(0xED, 0xB7, 0x28);     // #edb728
    private static readonly Color BarRed = Color.FromArgb(0xFB, 0x30, 0x41);       // #fb3041
    private static readonly Color CardBorder = Color.FromArgb(0xEB, 0xEB, 0xED);   // #ebebed
    private static readonly Color TextStrong = Color.FromArgb(0x37, 0x39, 0x3D);   // #37393d
    private static readonly Color TextPrimary = Color.FromArgb(0x49, 0x4B, 0x50);  // #494b50
    private static readonly Color TextSecondary = Color.FromArgb(0x85, 0x86, 0x8B);// #85868b
    private static readonly Color Hairline = Color.FromArgb(0xE9, 0xE9, 0xEB);     // #e9e9eb
    private static readonly Color BadgeBack = Color.FromArgb(0xE9, 0xE9, 0xEB);    // #e9e9eb
    private static readonly Color BadgeText = Color.FromArgb(0x77, 0x7A, 0x80);    // #777a80
    private static readonly Color TrackBack = Color.FromArgb(0xF0, 0xF1, 0xF2);    // #f0f1f2
    private static readonly Color SectionBack = Color.FromArgb(0xF4, 0xF4, 0xF5);  // #f4f4f5
    private static readonly Color StaleText = Color.FromArgb(0x91, 0x67, 0x1D);    // #91671d
    private static readonly Color UnlimitedBlue = Color.FromArgb(0x06, 0x73, 0xFF);// #0673ff
    private static readonly Color PageBack = Color.White;

    public MonitorForm(MonitoringCoordinator coordinator,
        Func<AppConfig>? appConfigGetter = null,
        Func<AppConfig, bool>? appConfigApplier = null)
    {
        _coordinator = coordinator;
        _appConfigGetter = appConfigGetter;
        _appConfigApplier = appConfigApplier;

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
        BuildSettingsView();

        // Dock priority: the fill views must sit EARLIER in the collection than the Top
        // header, so the header reserves its strip first and the views take the rest.
        Controls.Add(_quotaView);
        Controls.Add(_memoryView);
        Controls.Add(_settingsView);
        Controls.Add(_header);

        _coordinator.QuotaStateChanged += OnCoordinatorChanged;
        _coordinator.MemoryStateChanged += OnCoordinatorChanged;
        _coordinator.ReminderFired += OnReminderFired;

        FormClosed += (_, _) =>
        {
            _coordinator.QuotaStateChanged -= OnCoordinatorChanged;
            _coordinator.MemoryStateChanged -= OnCoordinatorChanged;
            _coordinator.ReminderFired -= OnReminderFired;
            _toolTip.Dispose();
        };

        ApplyLocalization();
        UpdateQuotaView();
        UpdateMemoryView();
        LoadSettingsFromCoordinator();

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
            const int dwmwcpRounded = 33;
            var preference = dwmwcpRounded;
            DwmSetWindowAttribute(Handle, dwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // pre-Win11 without dwmapi export — square corners are fine.
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
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
            Close();
        }
    }

    /// <summary>
    /// Tab / ← / → switch 额度↔内存 and 1/2/3 jump to a view — but only when the user is
    /// not typing into a text/numeric field on the settings page.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!IsTextInputActive())
        {
            switch (keyData)
            {
                case Keys.Tab or Keys.Right or Keys.Left when _currentView != View.Settings:
                    SetView(_currentView == View.Quota ? View.Memory : View.Quota);
                    return true;
                case Keys.D1 or Keys.NumPad1:
                    SetView(View.Quota);
                    return true;
                case Keys.D2 or Keys.NumPad2:
                    SetView(View.Memory);
                    return true;
                case Keys.D3 or Keys.NumPad3:
                    SetView(View.Settings);
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool IsTextInputActive() => ActiveControl is TextBox or NumericUpDown or ComboBox;

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

        // Data may have arrived while the popover had no handle; without this the panel
        // would show whatever the constructor rendered (possibly minutes old).
        if (_staleWhileHidden)
        {
            _staleWhileHidden = false;
            UpdateQuotaView();
            UpdateMemoryView();
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
        _refreshButton.Click += (_, _) => _coordinator.RequestManualRefreshAll();
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
        _settingsButton.Click += (_, _) => SetView(View.Settings);
        _settingsButton.AccessibleName = L("monitor.settings");
        _toolTip.SetToolTip(_settingsButton, L("monitor.settings"));

        _header.Controls.Add(_quotaUnderline);
        _header.Controls.Add(_quotaSegment);
        _header.Controls.Add(_memorySegment);
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
        _currentView = view;
        _quotaView.Visible = view == View.Quota;
        _memoryView.Visible = view == View.Memory;
        _settingsView.Visible = view == View.Settings;

        StyleSegment(_quotaSegment, view == View.Quota);
        StyleSegment(_memorySegment, view == View.Memory);
        _settingsButton.Text = view == View.Settings ? "✕" : "⚙";

        if (view == View.Quota) UpdateQuotaView();
        if (view == View.Memory) UpdateMemoryView();
        if (view == View.Settings) LoadSettingsFromCoordinator();
    }

    private void StyleSegment(Button segment, bool selected)
    {
        // Reference tabs: 18px text, bold + dark when selected, muted otherwise; the
        // 3px accent underline marks the active tab and hides on the settings view.
        segment.Font = new Font("Microsoft YaHei UI", 13.5F, selected ? FontStyle.Bold : FontStyle.Regular);
        segment.ForeColor = selected ? TextStrong : TextSecondary;
        LayoutTabs();
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

        if (_currentView == View.Settings)
        {
            _quotaUnderline.Visible = false;
            return;
        }
        var selected = _currentView == View.Memory ? _memorySegment : _quotaSegment;
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

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var card = new CardPanel
            {
                Width = 508,
                BackColor = Color.White,
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
            var status = new Label
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

    /// Coalesces coordinator events: several may fire within one UI cycle (three
    /// providers finishing together), and each one previously triggered a FULL rebuild of
    /// every card — the direct cause of the multi-second stalls on click/tab switches.
    /// <para>
    /// Two failure modes this flag must NOT have (both are what a naive bool would give):
    /// events arrive on coordinator threads, so the flag is set with <see cref="Interlocked"/>;
    /// and every exit path — including a disposed form or a <c>BeginInvoke</c> that throws
    /// because the handle died — resets it. A flag left stuck at "pending" silently
    /// disables ALL future refreshes.
    /// </para>
    /// </summary>
    private int _pendingUiRefresh;

    /// <summary>Data changed while the popover had no window handle; forces one repaint on next open.</summary>
    private bool _staleWhileHidden;

    private void OnCoordinatorChanged()
    {
        if (IsDisposed) return;
        if (Interlocked.Exchange(ref _pendingUiRefresh, 1) == 1) return;

        if (!IsHandleCreated)
        {
            // Nothing is on screen to paint, but the data DID move — remember it so the
            // next open does not show whatever the constructor rendered.
            _staleWhileHidden = true;
            Interlocked.Exchange(ref _pendingUiRefresh, 0);
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (IsDisposed) return;
                    UpdateQuotaView();
                    if (_currentView == View.Memory) UpdateMemoryView();
                }
                finally
                {
                    Interlocked.Exchange(ref _pendingUiRefresh, 0);
                }
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The handle died between the check and the post; never leave the gate closed.
            Interlocked.Exchange(ref _pendingUiRefresh, 0);
        }
    }

    private void OnReminderFired(QuotaReminderEvent evt)
    {
        // Reminders surface as tray balloons (owned by TrayApp); the panel just refreshes.
        OnCoordinatorChanged();
    }

    private void UpdateQuotaView()
    {
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
    }

    private void UpdateCard(ProviderDisplayState state)
    {
        var card = _cards[state.Provider];
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
        // plan tier sits next to the provider name; MiniMax reports none; the Ark tier
        // belongs to its product section header, not here.
        var tier = snapshot?.Buckets.FirstOrDefault(b => b.Tier is not null)?.Tier;
        badge.Text = state.Provider == ProviderId.Codex && tier is not null ? TierDisplay(tier) : "";
        var showBadge = badge.Text.Length > 0;
        badge.Visible = showBadge;
        badge.FitToText();

        // Status line BELOW the header rule, only for facts the user must know —
        // failure, staleness, disabled, paused, or nothing fetched yet. A healthy
        // card shows no status line at all.
        var parts = new List<string>();
        if (!state.Enabled) parts.Add(L("monitor.state_disabled") + " · " + L("monitor.state_disabled_hint"));
        if (hasFailure) parts.Add(L(ErrorKeyFor(state.LastAttempt!.Error)));
        if (state.Stale) parts.Add(L("monitor.state_stale"));
        if (state.PausedUntilUserRetry && state.Enabled) parts.Add(L("monitor.paused_short"));
        if (state.Enabled && snapshot is null && !hasFailure) parts.Add(L("monitor.state_no_data_yet"));
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
        statusLabel.SetBounds(S(13), S(51) + S(8), card.Width - 2 * S(13), S(18));

        // Rows start below the rule (or below the status line) — never at a fixed
        // offset from a control that may or may not be visible.
        var rowsY = S(51) + (showStatus ? S(8) + S(18) + S(4) : S(4));
        if (rowsPanel.Location != new Point(0, rowsY)) rowsPanel.Location = new Point(0, rowsY);
        if (rowsPanel.Width != card.Width) rowsPanel.Width = card.Width;

        var dimmed = state.Stale || hasFailure;

        // Build the row DESCRIPTORS first; the signature decides between an in-place
        // refresh and a structural rebuild (see RowDesc note).
        var rows = new List<RowDesc>();

        if (!state.Enabled)
        {
            // The status line under the header carries the whole message (disabled +
            // hint); an inert card renders no metric rows at all.
        }
        else if (snapshot is null)
        {
            // Same — "尚未获取数据" or the failure reason is already on the status line.
        }
        else
        {
            foreach (var bucket in snapshot.Buckets)
            {
                if (state.Provider == ProviderId.Ark)
                {
                    // Product level lives HERE — "Coding Plan [Pro]" as a full-width
                    // section strip, like the reference. Never next to the provider
                    // name, never flattened into a window row.
                    rows.Add(new RowDesc(ArkProductDisplay(bucket), "", null, null, "",
                        dimmed, Badge: bucket.Tier is { } productTier ? TierDisplay(productTier) : "",
                        SectionHeader: true));
                }

                if (bucket.Error is not null)
                {
                    rows.Add(MetricRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.bucket_error"),
                        "", null, null, "", true));
                    continue;
                }
                if (bucket.Subscribed == false)
                {
                    rows.Add(MetricRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.no_subscription"),
                        "", null, null, "", true));
                    continue;
                }
                if (bucket.Windows.Count == 0)
                {
                    rows.Add(MetricRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.not_returned"),
                        "", null, null, "", true));
                    continue;
                }

                foreach (var window in bucket.Windows)
                {
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
                        ResetText(window), dimmed, valueColor));
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
                    Badge: creditsTitle == L("monitor.badge.reset_credit") ? "" : L("monitor.badge.reset_credit")));

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
    private void ApplyCardRows(ProviderId id, FlowLayoutPanel rowsPanel, List<RowDesc> rows)
    {
        // Clickable is part of the signature: it decides whether the row gets a click
        // handler, and an in-place refresh never touches handlers — a row that becomes
        // clickable (or stops being one) MUST be rebuilt, not refreshed in place.
        // Badge/ValueColor/SectionHeader/SeparatorAbove decide STRUCTURE, so they are in
        // the signature too; a differing title/value/rebuilds via the same rule.
        var signature = string.Join("|", rows.Select(r =>
            $"{r.Title}#{r.Percent}#{r.Fraction}#{r.BarColor}#{r.Right}#{r.Dimmed}#{r.Clickable}#{r.Badge}#{r.ValueColor}#{r.SectionHeader}#{r.SeparatorAbove}"));

        if (_cardSignatures.TryGetValue(id, out var previous) &&
            previous == signature &&
            _cardRowControls.TryGetValue(id, out var existing) &&
            existing.Count == rows.Count)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                UpdateRowInPlace(existing[i], rows[i]);
            }
            return;
        }

        _cardSignatures[id] = signature;
        ClearRows(rowsPanel);
        var controls = new List<Control>(rows.Count);
        rowsPanel.SuspendLayout();
        foreach (var desc in rows)
        {
            var row = AddRow(rowsPanel, desc);
            if (desc.Clickable) MakeClickable(row, ToggleCreditsExpansion);
            controls.Add(row);
        }
        rowsPanel.ResumeLayout(true);
        _cardRowControls[id] = controls;
    }

    private static void UpdateRowInPlace(Control row, RowDesc desc)
    {
        foreach (Control child in row.Controls)
        {
            switch (child.Tag)
            {
                case "t" when child.Text != desc.Title:
                    child.Text = desc.Title;
                    break;
                case "p" when child.Text != desc.Percent:
                    child.Text = desc.Percent;
                    break;
                case "r" when child.Text != desc.Right:
                    child.Text = desc.Right;
                    break;
                case "b" when child is QuotaBar bar:
                    if (Math.Abs(bar.Fraction - (desc.Fraction ?? 0)) > 0.0001) bar.Fraction = desc.Fraction ?? 0;
                    if (desc.BarColor is { } color && bar.FillColor != color)
                    {
                        bar.FillColor = color;
                        bar.Invalidate();
                    }
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

    private static void ClearRows(FlowLayoutPanel rowsPanel)
    {
        var old = rowsPanel.Controls.OfType<Control>().ToList();
        rowsPanel.Controls.Clear();
        foreach (var c in old) c.Dispose();
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
        var rowWidth = Math.Max(200, rowsPanel.Width);

        if (desc.SectionHeader)
        {
            // Product strip ("Coding Plan [Pro]"): full card width, light gray, 36 high.
            var strip = new Panel
            {
                Width = rowWidth,
                Height = S(36),
                Margin = new Padding(0),
                BackColor = SectionBack,
            };
            var labelFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            var labelWidth = TextRenderer.MeasureText(desc.Title, labelFont).Width;
            var label = new Label
            {
                Text = desc.Title,
                Tag = "t",
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = SectionBack,
                ForeColor = TextStrong,
                Font = labelFont,
                Location = new Point(S(13), (S(36) - S(20)) / 2),
                Size = new Size(Math.Min(labelWidth + 2, rowWidth - 2 * S(13)), S(20)),
            };
            strip.Controls.Add(label);
            if (desc.Badge.Length > 0)
            {
                var productBadge = NewRowBadge(desc.Badge);
                productBadge.Left = Math.Min(label.Right + S(8), rowWidth - S(13) - productBadge.Width);
                productBadge.Top = (S(36) - productBadge.Height) / 2;
                strip.Controls.Add(productBadge);
            }
            rowsPanel.Controls.Add(strip);
            return strip;
        }

        var hasBar = desc.Fraction is { } && desc.BarColor is { };
        var rowHeight = hasBar ? S(54) : S(40);
        var row = new Panel
        {
            Width = rowWidth,
            Height = rowHeight,
            Margin = new Padding(0),
            BackColor = Color.White,
        };
        if (desc.SeparatorAbove)
        {
            var separatorPen = new Pen(Hairline);
            row.Paint += (_, e) => e.Graphics.DrawLine(separatorPen, S(13), 0, rowWidth, 0);
            row.Disposed += (_, _) => separatorPen.Dispose();
        }

        // Reference column geometry over the content width (row minus the 13-logical
        // side insets): 144 name / flexible value / 106 time, 12-logical gaps.
        var inset = S(13);
        var contentWidth = rowWidth - 2 * inset;
        var nameWidth = (int)Math.Round(contentWidth * (144.0 / 482.0));
        var rightWidth = (int)Math.Round(contentWidth * (106.0 / 482.0));
        var gap = S(12);
        var valueX = inset + nameWidth + gap;
        var valueWidth = Math.Max(S(60), contentWidth - nameWidth - rightWidth - 2 * gap);
        var rightX = rowWidth - inset - rightWidth;
        var lineHeight = S(20);

        // Title + window badge share the name column: the badge sits right after the
        // measured text when both fit; otherwise the title ellipsizes and the badge
        // docks at the column end ("Codex Spa…" is exactly what we must NOT ship).
        // The +6 slack covers Label's internal padding — without it a text that fits
        // its measure exactly still ellipsizes. Rows with NO badge, value and time
        // (expanded credit details) are single full-width lines instead.
        var measuredTitle = TextRenderer.MeasureText(desc.Title, Font).Width;
        var fullLineRow = desc.Badge.Length == 0 && desc.Percent.Length == 0 && desc.Right.Length == 0;
        var rowBadge = desc.Badge.Length > 0 ? NewRowBadge(desc.Badge) : null;
        var badgeGap = S(6); // reference .metric-label gap: 6 — tighter than the column gap
        var badgeSpace = rowBadge is null ? 0 : rowBadge.Width + badgeGap;
        var titleAvailable = fullLineRow
            ? contentWidth
            : Math.Max(lineHeight, nameWidth - badgeSpace);
        var titleLabel = new Label
        {
            Text = desc.Title,
            Tag = "t",
            AutoSize = false,
            AutoEllipsis = !fullLineRow,
            BackColor = row.BackColor,
            ForeColor = desc.Dimmed ? TextSecondary : TextPrimary,
        };
        var titleY = (rowHeight - lineHeight) / 2;
        titleLabel.SetBounds(inset, titleY, Math.Min(measuredTitle + S(8), titleAvailable), lineHeight);
        row.Controls.Add(titleLabel);
        if (rowBadge is not null)
        {
            rowBadge.Left = measuredTitle <= titleAvailable
                ? Math.Min(titleLabel.Right + badgeGap, inset + nameWidth - rowBadge.Width)
                : inset + nameWidth - rowBadge.Width;
            rowBadge.Top = (rowHeight - rowBadge.Height) / 2;
            row.Controls.Add(rowBadge);
        }

        // VALUE column: text on the first line, 7px bar underneath — or a single
        // vertically centered line for compact rows (unlimited, counts, credits).
        // Empty labels are not added at all: a painted white rectangle would sit ON
        // TOP of a full-width title and erase its tail.
        var valueLabel = new Label
        {
            Text = desc.Percent,
            Tag = "p",
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = row.BackColor,
            ForeColor = desc.ValueColor ?? (desc.Dimmed ? TextSecondary : TextPrimary),
        };
        if (hasBar)
        {
            valueLabel.SetBounds(valueX, S(12), valueWidth, S(18));
            row.Controls.Add(valueLabel);

            var bar = new QuotaBar
            {
                Tag = "b",
                Fraction = desc.Fraction ?? 0,
                FillColor = desc.BarColor ?? BarGreen,
                BackColor = row.BackColor,
            };
            bar.SetBounds(valueX, S(35), valueWidth, S(7));
            row.Controls.Add(bar);
        }
        else if (desc.Percent.Length > 0)
        {
            valueLabel.SetBounds(valueX, (rowHeight - lineHeight) / 2, valueWidth, lineHeight);
            row.Controls.Add(valueLabel);
        }

        if (desc.Right.Length > 0)
        {
            var rightLabel = new Label
            {
                Text = desc.Right,
                Tag = "r",
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = row.BackColor,
                ForeColor = TextSecondary,
                Font = new Font("Microsoft YaHei UI", 8F),
            };
            rightLabel.SetBounds(rightX, (rowHeight - lineHeight) / 2, rightWidth, lineHeight);
            row.Controls.Add(rightLabel);
        }

        rowsPanel.Controls.Add(row);
        return row;
    }

    /// <summary>Window badge for a metric row — smaller type than the header tier badge.</summary>
    private BadgeLabel NewRowBadge(string text)
    {
        var badge = new BadgeLabel { Font = new Font("Microsoft YaHei UI", 7.5F) };
        badge.Text = text;
        badge.FitToText();
        badge.Height = S(18);
        return badge;
    }

    private void SizeCard(Panel card, FlowLayoutPanel rowsPanel)
    {
        // Force the flow panel to lay out NOW so its Height reflects the fresh rows —
        // reading it before layout returns the stale (too tall/short) value.
        rowsPanel.PerformLayout();
        card.Height = rowsPanel.Location.Y + rowsPanel.Height + S(12);
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

        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(14, 12, 14, 12),
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

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_memoryStateLabel, 0, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(statsGrid, 0, 1);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_memorySampledLabel, 0, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_trendChart, 0, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_taskManagerButton, 0, 4);

        card.Controls.Add(layout);
        _memoryView.Controls.Add(card);
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

        // Stat grid: 左列物理、右列提交/信号 (Windows 机制，不仿造 macOS 压力指标).
        if (sample is not null)
        {
            _memoryStats[0, 0].Text = $"{L("monitor.memory.stat_physical")}  {FormatBytes(sample.PhysicalTotalBytes)}";
            _memoryStats[0, 1].Text = $"{L("monitor.memory.stat_commit")}  {FormatBytes(sample.CommitTotalBytes)}";
            _memoryStats[1, 0].Text = $"{L("monitor.memory.stat_used")}  {FormatBytes(sample.PhysicalUsedBytes)}";
            _memoryStats[1, 1].Text = $"{L("monitor.memory.stat_commit_limit")}  {FormatBytes(sample.CommitLimitBytes)}";
            _memoryStats[2, 0].Text = $"{L("monitor.memory.stat_available")}  {FormatBytes(sample.PhysicalAvailableBytes)}";
            _memoryStats[2, 1].Text = $"{L("monitor.memory.stat_low_signal")}  {sample.LowMemorySignal switch
            {
                true => L("monitor.low_triggered"),
                false => L("monitor.low_not_triggered"),
                null => L("monitor.low_unknown"),
            }}";
            _memorySampledLabel.Text = L("monitor.memory_sampled", FormatTime(sample.SampledAtUtc));
        }
        else
        {
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    _memoryStats[row, col].Text = L("monitor.not_provided");
                }
            }
            _memorySampledLabel.Text = L("monitor.memory_sampled", "—");
        }

        _trendChart.SetSamples(_coordinator.MemoryHistory.Snapshot());
    }

    // ---------- settings view ----------

    private void BuildSettingsView()
    {
        // TableLayoutPanel with AutoSize rows — the previous hand-computed y offsets
        // broke under DPI scaling (controls overlapped/vanished on a real 150% machine).
        _settingsView.Dock = DockStyle.Fill;
        _settingsView.Padding = new Padding(12, 6, 12, 12);
        _settingsView.BackColor = PageBack;

        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(14, 12, 14, 12),
        };

        var scroll = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.White,
            Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var rowIndex = 0;
        void AddRow(Control c, int topMargin = 0, int bottomMargin = 6, int leftIndent = 0)
        {
            c.Margin = new Padding(leftIndent, topMargin, 0, bottomMargin);
            c.AutoSize = true;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c, 0, rowIndex++);
        }

        Label Section(string key)
        {
            var label = new Label
            {
                Text = L(key),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = SystemColors.ControlText,
            };
            AddRow(label, topMargin: 8, bottomMargin: 2);
            return label;
        }

        FlowLayoutPanel RowOf(params Control[] items)
        {
            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6),
            };
            foreach (var item in items) flow.Controls.Add(item);
            return flow;
        }

        // —— 息屏模式（原 OB Dim 设置） ——
        Section("monitor.settings.group_screen");
        AddRow(new Label { Text = L("settings.work_mode"), AutoSize = true }, bottomMargin: 2);

        foreach (var nud in new[] { _workAc, _workDc, _awayAc, _awayDc })
        {
            nud.Width = 64;
            nud.Minimum = 0;
            nud.Maximum = 99999;
        }
        AddRow(RowOf(_workAc, _workDc));

        AddRow(new Label { Text = L("settings.away_mode"), AutoSize = true }, bottomMargin: 2);
        AddRow(RowOf(_awayAc, _awayDc));

        // —— 热键 ——
        Section("monitor.settings.group_hotkeys");
        AddRow(new Label { Text = L("settings.hotkey_label"), AutoSize = true }, bottomMargin: 2);
        _switchHotkeyBox.Width = 200;
        _switchHotkeyBox.ReadOnly = true;
        AddRow(_switchHotkeyBox, bottomMargin: 4);

        AddRow(new Label { Text = L("monitor.settings.popover_hotkey"), AutoSize = true }, bottomMargin: 2);
        _popoverHotkeyBox.Width = 200;
        _popoverHotkeyBox.ReadOnly = true;
        AddRow(_popoverHotkeyBox, bottomMargin: 2);

        AddRow(new Label
        {
            Text = L("settings.win_note"),
            AutoSize = true,
            ForeColor = TextSecondary,
            Font = new Font(Font.FontFamily, 7.5F),
        }, bottomMargin: 2);

        _leftClickCheck.AutoSize = true;
        AddRow(_leftClickCheck, topMargin: 4);

        // —— 通用 ——
        // Language sits at the TOP of the general group (2026-09-13 layout review).
        Section("monitor.settings.group_general");
        var languageLabel = new Label { Text = L("settings.language"), AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        _languageBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageBox.Width = 110;
        _languageBox.Items.AddRange(new object[] { "中文", "English" });
        AddRow(RowOf(languageLabel, _languageBox));

        _memoryCheck.AutoSize = true;
        AddRow(_memoryCheck);

        _autoStartCheck = new CheckBox { AutoSize = true, Text = L("settings.autostart") };
        AddRow(_autoStartCheck);

        // —— 监控 ——
        Section("monitor.settings.group_monitoring");
        _remindersCheck.AutoSize = true;
        AddRow(_remindersCheck);

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var enable = new CheckBox
            {
                Name = $"enable_{id}",
                AutoSize = true,
                Text = L($"monitor.provider.{id.ToString().ToLowerInvariant()}"),
            };
            _enableChecks[id] = enable;
            AddRow(enable, topMargin: 4, bottomMargin: 2);

            var path = new TextBox
            {
                Name = $"cliPath_{id}",
                Width = 340,
                Margin = new Padding(20, 0, 0, 6),
            };
            _toolTip.SetToolTip(path, L("monitor.settings.cli_path"));
            _pathBoxes[id] = path;
            AddRow(path);
        }

        // —— 保存 ——
        _saveButton.AutoSize = true;
        _saveButton.MinimumSize = new Size(96, 0); // never a sliver of a click target
        _saveButton.FlatStyle = FlatStyle.Flat;
        _saveButton.FlatAppearance.BorderSize = 0;
        _saveButton.BackColor = Accent;
        _saveButton.ForeColor = Color.White;
        _saveButton.Padding = new Padding(12, 2, 12, 2);
        _saveButton.Cursor = Cursors.Hand;
        _saveButton.Click += (_, _) => ApplySettingsFromView();
        AddRow(_saveButton, topMargin: 8, bottomMargin: 0);

        _saveStatusLabel.AutoSize = true;
        _saveStatusLabel.ForeColor = TextSecondary;
        _saveStatusLabel.Margin = new Padding(12, 8, 0, 0);
        AddRow(_saveStatusLabel, bottomMargin: 0);

        scroll.Controls.Add(layout);
        card.Controls.Add(scroll);
        _settingsView.Controls.Add(card);
    }

    private CheckBox _autoStartCheck = null!;

    private void LoadSettingsFromCoordinator()
    {
        var settings = _coordinator.Settings;
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var provider = settings.Provider(id);
            _enableChecks[id].Checked = provider.Enabled;
            _pathBoxes[id].Text = provider.CliPath ?? "";
        }
        _memoryCheck.Checked = settings.MemoryEnabled;
        _remindersCheck.Checked = settings.RemindersEnabled;
        _leftClickCheck.Checked = settings.LeftClickOpensPopover;
        _popoverHotkeyBox.SetCaptured(
            GlobalHotkeyService.ParseHotkeyString(settings.PopoverHotkey)
            ?? GlobalHotkeyService.ParseHotkeyString("Ctrl+Alt+D")!);
        _saveStatusLabel.Text = "";

        if (_appConfigGetter is { } getter)
        {
            var appCfg = getter();
            _workAc.Value = appCfg.Work.AcMinutes;
            _workDc.Value = appCfg.Work.DcMinutes;
            _awayAc.Value = appCfg.Away.AcMinutes;
            _awayDc.Value = appCfg.Away.DcMinutes;
            _switchHotkeyBox.SetCaptured(appCfg.Hotkey);
            _autoStartCheck.Checked = appCfg.AutoStart;
            _languageBox.SelectedIndex = appCfg.Language == "en-US" ? 1 : 0;
        }
        else
        {
            // Test/detached mode: the app-settings section is inert but must not LOOK
            // broken — show the defaults instead of blanks.
            foreach (var nud in new[] { _workAc, _workDc, _awayAc, _awayDc })
            {
                nud.Enabled = false;
            }
            _workAc.Value = 0;
            _workDc.Value = 30;
            _awayAc.Value = 1;
            _awayDc.Value = 1;
            _switchHotkeyBox.SetCaptured(new HotkeyConfig());
            _switchHotkeyBox.Enabled = false;
            _autoStartCheck.Checked = true;
            _autoStartCheck.Enabled = false;
            _languageBox.SelectedIndex = 0;
            _languageBox.Enabled = false;
        }
    }

    private void ApplySettingsFromView()
    {
        var popoverHotkey = $"{_popoverHotkeyBox.Captured.Modifiers}+{_popoverHotkeyBox.Captured.Key}";
        var settings = _coordinator.Settings with
        {
            MemoryEnabled = _memoryCheck.Checked,
            RemindersEnabled = _remindersCheck.Checked,
            LeftClickOpensPopover = _leftClickCheck.Checked,
            PopoverHotkey = popoverHotkey,
            Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
            {
                Id = id,
                Enabled = _enableChecks[id].Checked,
                CliPath = string.IsNullOrWhiteSpace(_pathBoxes[id].Text) ? null : _pathBoxes[id].Text.Trim(),
            }).ToList(),
        };

        // Apply first so the views update even if a save fails (a failed save is reported,
        // never faked — spec §8(6)). Monitoring settings and app settings persist
        // independently (monitoring.json vs config.json).
        _coordinator.ApplySettings(settings);
        var monitoringSaved = _coordinator.SaveSettings(settings);

        var appSaved = true;
        if (_appConfigGetter is { } getter && _appConfigApplier is { } applier)
        {
            var current = getter();
            var newCfg = current with
            {
                Work = new TimeoutConfig { AcMinutes = (int)_workAc.Value, DcMinutes = (int)_workDc.Value },
                Away = new TimeoutConfig { AcMinutes = (int)_awayAc.Value, DcMinutes = (int)_awayDc.Value },
                Hotkey = _switchHotkeyBox.Captured,
                AutoStart = _autoStartCheck.Checked,
                Language = _languageBox.SelectedIndex == 1 ? "en-US" : "zh-CN",
            };
            appSaved = applier(newCfg);
        }

        _saveStatusLabel.Text = monitoringSaved && appSaved ? L("monitor.saved") : L("monitor.settings.save_failed");
    }

    // ---------- localization refresh ----------

    private void ApplyLocalization()
    {
        Text = L("monitor.title");
        _quotaSegment.Text = L("monitor.tab_quota");
        _memorySegment.Text = L("monitor.tab_memory");
        _refreshButton.Text = "⟳";
        _settingsButton.Text = "⚙";
        _taskManagerButton.Text = L("monitor.open_task_manager");
        _saveButton.Text = L("monitor.settings.save");
        _memoryCheck.Text = L("monitor.settings.memory");
        _remindersCheck.Text = L("monitor.settings.reminders");
        _leftClickCheck.Text = L("monitor.settings.left_click");
        _trendChart.Title = L("monitor.trend_title");
        _trendChart.SeriesNames = (L("monitor.trend_physical"), L("monitor.trend_commit"));
        _trendChart.EmptyText = L("monitor.trend_no_data");
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (_cardTitles.TryGetValue(id, out var title))
            {
                title.Text = L($"monitor.provider.{id.ToString().ToLowerInvariant()}");
            }
        }
        LayoutTabs();
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
        public double Fraction { get; set; }
        public Color FillColor { get; set; } = BarGreen;

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
            TextRenderer.DrawText(g, Text, Font, rect, BadgeText,
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

    /// <summary>
    /// Draws the one-hour trend as TWO stacked area charts — physical available and commit —
    /// each with its own byte scale. They never share a unitless axis (spec §6). Missing
    /// samples (sleep, disabled sampling) break the line instead of being interpolated.
    /// </summary>
    internal sealed class MemoryTrendChart : Panel
    {
        public MemorySample[] Samples { get; set; } = [];
        public string Title { get; set; } = "";
        public (string Physical, string Commit) SeriesNames { get; set; } = ("", "");
        public string EmptyText { get; set; } = "";

        private const int GapBreakThresholdSeconds = 15;
        private const int MaxDrawPoints = 240;

        private int _lastSampleCount = -1;
        private DateTimeOffset _lastSampleStamp;
        private Pen? _linePen;

        public MemoryTrendChart()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
        }

        /// <summary>
        /// Assigns samples and repaints — SKIPPED entirely when nothing changed (same
        /// count and same last stamp). The coordinator samples every 5 s and events fire
        /// on every view switch; repainting 720-point curves for identical data was pure
        /// waste on the UI thread.
        /// </summary>
        public void SetSamples(MemorySample[] samples)
        {
            var last = samples.Length > 0 ? samples[^1].SampledAtUtc : DateTimeOffset.MinValue;
            if (samples.Length == _lastSampleCount && last == _lastSampleStamp) return;
            _lastSampleCount = samples.Length;
            _lastSampleStamp = last;
            Samples = samples;
            Invalidate();
        }

        /// <summary>Releases the cached pen — a Pen holds a GDI handle, so caching it is
        /// only safe while the control also owns its disposal.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _linePen?.Dispose();
                _linePen = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);

            var textBrush = SystemBrushes.ControlText;
            using var titleFont = new Font(Font, FontStyle.Bold);
            g.DrawString(Title, titleFont, textBrush, 4, 2);

            var chartTop = 24;
            var chartHeight = (Height - chartTop - 24) / 2 - 6;
            if (chartHeight < 30 || Samples.Length == 0)
            {
                g.DrawString(EmptyText, Font, textBrush, 4, chartTop + 8);
                return;
            }

            _linePen ??= new Pen(Accent, 1.6f);
            DrawSeries(g, chartTop, chartHeight, s => (double)s.PhysicalAvailableBytes, SeriesNames.Physical);
            DrawSeries(g, chartTop + chartHeight + 12, chartHeight, s => (double)s.CommitTotalBytes, SeriesNames.Commit);
        }

        private void DrawSeries(Graphics g, int top, int height, Func<MemorySample, double> value, string name)
        {
            var bounds = new Rectangle(52, top, Width - 64, height);
            using var borderPen = new Pen(CardBorder);
            g.DrawRectangle(borderPen, bounds);

            var now = DateTimeOffset.UtcNow;
            var windowStart = now.AddHours(-1);

            // Downsample: GDI+ happily draws 720 points but the per-point cost adds up at
            // 150% DPI with two repaints per second-class interaction; 240 points is far
            // past visual resolution for a 1-hour chart. The LAST sample is always kept.
            var relevant = Samples.Where(s => s.SampledAtUtc >= windowStart).ToList();
            List<MemorySample> points;
            if (relevant.Count > MaxDrawPoints)
            {
                var stride = (int)Math.Ceiling(relevant.Count / (double)MaxDrawPoints);
                points = [];
                for (var i = 0; i < relevant.Count; i += stride) points.Add(relevant[i]);
                if (points[^1] != relevant[^1]) points.Add(relevant[^1]);
            }
            else
            {
                points = relevant;
            }

            double min = double.MaxValue, max = double.MinValue;
            foreach (var s in points)
            {
                var v = value(s);
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (min > max)
            {
                return;
            }
            if (max - min < 1)
            {
                max = min + 1;
            }
            var pad = (max - min) * 0.08;
            min = Math.Max(0, min - pad);
            max += pad;

            float X(DateTimeOffset t) => bounds.Left + (float)((t - windowStart) / TimeSpan.FromHours(1)) * bounds.Width;
            float Y(double v) => bounds.Bottom - (float)((v - min) / (max - min)) * bounds.Height;

            var previous = default(MemorySample);
            var segments = new List<List<PointF>>();
            var current = new List<PointF>();
            foreach (var s in points)
            {
                if (previous is not null &&
                    (s.SampledAtUtc - previous.SampledAtUtc).TotalSeconds > GapBreakThresholdSeconds)
                {
                    if (current.Count > 1) segments.Add(current);
                    current = [];
                }
                current.Add(new PointF(X(s.SampledAtUtc), Y(value(s))));
                previous = s;
            }
            if (current.Count > 1) segments.Add(current);

            foreach (var segment in segments)
            {
                g.DrawLines(_linePen!, [.. segment]);

                // Soft area fill under the curve.
                var fillPoints = new List<PointF>(segment)
                {
                    new(segment[^1].X, bounds.Bottom),
                    new(segment[0].X, bounds.Bottom),
                };
                using var fillBrush = new LinearGradientBrush(
                    new RectangleF(bounds.Left, top, bounds.Width, height),
                    Color.FromArgb(40, Accent), Color.FromArgb(8, Accent), LinearGradientMode.Vertical);
                g.FillPolygon(fillBrush, [.. fillPoints]);

                // A lone sample (fresh start) would otherwise render as an empty chart.
                var lastPoint = segment[^1];
                using var dotBrush = new SolidBrush(Accent);
                g.FillEllipse(dotBrush, lastPoint.X - 3, lastPoint.Y - 3, 6, 6);
            }

            // Axis labels: min/max of THIS series only, in bytes — never a shared unitless axis.
            g.DrawString(FormatBytes((ulong)max), Font, SystemBrushes.ControlText, 2, top - 2);
            g.DrawString(FormatBytes((ulong)Math.Max(0, min)), Font, SystemBrushes.ControlText, 2, bounds.Bottom - 14);
            g.DrawString(name, Font, SystemBrushes.ControlText, bounds.Left + 4, top + 2);
        }
    }
}
