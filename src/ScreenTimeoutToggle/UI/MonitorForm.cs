using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;

namespace OBDim.UI;

/// <summary>
/// The "Usage &amp; Memory" popover: a borderless panel anchored to its tray icon — the
/// macOS menu-bar-popover interaction the user asked for (2026-09-13 feedback). One
/// instance at a time; closes on Esc, on losing activation (click outside), or via the
/// tray menu toggle. Closing the popover does NOT stop the coordinator — monitoring keeps
/// running in the background; only app exit does.
/// <para>
/// Three views inside one panel: 额度 (quota cards), 内存 (memory stats + trend), 设置
/// (inline monitoring settings — deliberately NOT a separate dialog). Keyboard: the
/// global popover hotkey toggles the panel; Tab / ← / → switch 额度↔内存; 1/2/3 jump
/// directly; Esc closes.
/// </para>
/// </summary>
public sealed class MonitorForm : Form
{
    private readonly MonitoringCoordinator _coordinator;

    private enum View { Quota, Memory, Settings }

    private View _currentView = View.Quota;

    // Header controls
    private readonly Panel _header = new();
    private readonly Button _quotaSegment = new();
    private readonly Button _memorySegment = new();
    private readonly Button _refreshButton = new();
    private readonly Button _settingsButton = new();

    // Views
    private readonly FlowLayoutPanel _quotaView = new();
    private readonly Panel _memoryView = new();
    private readonly Panel _settingsView = new();

    // Quota cards (one per provider)
    private readonly Dictionary<ProviderId, Panel> _cards = [];
    private readonly Dictionary<ProviderId, Label> _cardTitles = [];
    private readonly Dictionary<ProviderId, BadgeLabel> _cardBadges = [];
    private readonly Dictionary<ProviderId, Label> _cardStates = [];
    private readonly Dictionary<ProviderId, FlowLayoutPanel> _cardRows = [];

    // Memory view controls
    private readonly Label _memoryStateLabel = new();
    private readonly Label _memorySampledLabel = new();
    private readonly Label _memoryPhysicalLabel = new();
    private readonly Label _memoryAvailableLabel = new();
    private readonly Label _memoryCommitLabel = new();
    private readonly Label _memorySignalLabel = new();
    private readonly MemoryTrendChart _trendChart = new();
    private readonly Button _taskManagerButton = new();

    // Settings view controls
    private readonly Dictionary<ProviderId, CheckBox> _enableChecks = [];
    private readonly Dictionary<ProviderId, TextBox> _pathBoxes = [];
    private readonly CheckBox _memoryCheck = new();
    private readonly CheckBox _remindersCheck = new();
    private readonly Label _hotkeyHint = new();
    private readonly Button _saveButton = new();
    private readonly Label _saveStatusLabel = new();

    private readonly ToolTip _toolTip = new();

    private static readonly Color Accent = Color.FromArgb(0, 122, 255);     // macOS blue
    private static readonly Color BarGreen = Color.FromArgb(52, 199, 89);
    private static readonly Color BarAmber = Color.FromArgb(255, 159, 10);
    private static readonly Color BarRed = Color.FromArgb(255, 59, 48);
    private static readonly Color CardBorder = Color.FromArgb(229, 229, 234);
    private static readonly Color PageBack = Color.FromArgb(246, 246, 248);

    public MonitorForm(MonitoringCoordinator coordinator)
    {
        _coordinator = coordinator;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BackColor = PageBack;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(400, 560);

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
    }

    private static string L(string key, params object[] args) => LocalizationService.Get(key, args);

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

    /// <summary>Popover behaviour: any loss of activation (click outside) dismisses the panel.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible) Close();
    }

    /// <summary>Tab / ← / → switch 额度↔内存; 1/2/3 jump to a view directly.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Tab or Keys.Right or Keys.Left:
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
        return base.ProcessCmdKey(ref msg, keyData);
    }

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
    }

    private Point ComputeAnchorFromIcon(Rect iconRect)
    {
        var screen = Screen.FromRectangle(new Rectangle(iconRect.Left, iconRect.Top,
            Math.Max(1, iconRect.Right - iconRect.Left), Math.Max(1, iconRect.Bottom - iconRect.Top)));
        var work = screen.WorkingArea;
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
        var x = Cursor.Position.X + 12 - Width;
        var y = work.Bottom - Height - 8;
        return ClampToWorkArea(x, y, work);
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
            // NotifyIcon keeps its Shell_NotfiyIcon id and message window privately; the
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
        // Explicit size before docking so right-anchored children compute their offsets
        // against the final width.
        _header.Size = new Size(400, 52);
        _header.Dock = DockStyle.Top;
        _header.BackColor = PageBack;

        _quotaSegment.AutoSize = false;
        _quotaSegment.Size = new Size(62, 28);
        _quotaSegment.Location = new Point(12, 11);
        _quotaSegment.FlatStyle = FlatStyle.Flat;
        _quotaSegment.FlatAppearance.BorderSize = 0;
        _quotaSegment.Click += (_, _) => SetView(View.Quota);

        _memorySegment.AutoSize = false;
        _memorySegment.Size = new Size(62, 28);
        _memorySegment.Location = new Point(74, 11);
        _memorySegment.FlatStyle = FlatStyle.Flat;
        _memorySegment.FlatAppearance.BorderSize = 0;
        _memorySegment.Click += (_, _) => SetView(View.Memory);

        _refreshButton.AutoSize = false;
        _refreshButton.Size = new Size(32, 30);
        _refreshButton.Location = new Point(316, 9);
        _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshButton.FlatStyle = FlatStyle.Flat;
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.BackColor = PageBack;
        _refreshButton.Font = new Font("Segoe UI Symbol", 12F);
        _refreshButton.Click += (_, _) => _coordinator.RequestManualRefreshAll();
        _refreshButton.AccessibleName = L("monitor.refresh_all");
        _toolTip.SetToolTip(_refreshButton, L("monitor.refresh_all"));

        _settingsButton.AutoSize = false;
        _settingsButton.Size = new Size(32, 30);
        _settingsButton.Location = new Point(356, 9);
        _settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsButton.FlatStyle = FlatStyle.Flat;
        _settingsButton.FlatAppearance.BorderSize = 0;
        _settingsButton.BackColor = PageBack;
        _settingsButton.Font = new Font("Segoe UI Symbol", 11F);
        _settingsButton.Click += (_, _) => SetView(View.Settings);
        _settingsButton.AccessibleName = L("monitor.settings");
        _toolTip.SetToolTip(_settingsButton, L("monitor.settings"));

        _header.Controls.Add(_quotaSegment);
        _header.Controls.Add(_memorySegment);
        _header.Controls.Add(_refreshButton);
        _header.Controls.Add(_settingsButton);

        // The gray track behind the two segment buttons.
        _header.Paint += (_, e) =>
        {
            var segRect = new Rectangle(8, 8, 132, 34);
            using var brush = new SolidBrush(Color.FromArgb(232, 232, 236));
            using var path = RoundedPath(segRect, 8);
            e.Graphics.FillPath(brush, path);
        };
    }

    private void SetView(View view)
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
        segment.BackColor = selected ? Color.White : PageBack;
        segment.Invalidate();
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
        _quotaView.Padding = new Padding(10, 4, 10, 10);

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var card = new CardPanel
            {
                Width = 372,
                BackColor = Color.White,
                Padding = new Padding(12, 10, 12, 10),
                Margin = new Padding(0, 0, 0, 10),
            };

            var title = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(12, 10),
                Text = L($"monitor.provider.{id.ToString().ToLowerInvariant()}"),
            };
            var badge = new BadgeLabel
            {
                Location = new Point(title.Right + 8, 12),
            };
            var state = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(142, 142, 147),
                Location = new Point(12, 32),
            };
            var rows = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Location = new Point(4, 54),
                Width = 348,
                Margin = new Padding(0),
            };

            card.Controls.Add(title);
            card.Controls.Add(badge);
            card.Controls.Add(state);
            card.Controls.Add(rows);
            card.Tag = (title, badge, state, rows);

            _cards[id] = card;
            _cardTitles[id] = title;
            _cardBadges[id] = badge;
            _cardStates[id] = state;
            _cardRows[id] = rows;
            _quotaView.Controls.Add(card);
        }
    }

    private void OnCoordinatorChanged()    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            UpdateQuotaView();
            if (_currentView == View.Memory) UpdateMemoryView();
        });
    }

    private void OnReminderFired(QuotaReminderEvent evt)
    {
        // Reminders surface as tray balloons (owned by TrayApp); the panel just refreshes.
        if (IsDisposed) return;
        BeginInvoke(UpdateQuotaView);
    }

    private void UpdateQuotaView()
    {
        if (IsDisposed) return;
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
        var statusLabel = _cardStates[state.Provider];
        var rowsPanel = _cardRows[state.Provider];

        var snapshot = state.LastGood;
        var hasFailure = state.LastAttempt is { HasError: true };

        // Status line: refresh state, failure and freshness are independent facts (§5.3).
        var parts = new List<string>();
        if (!state.Enabled) parts.Add(L("monitor.state_disabled"));
        if (state.Refreshing) parts.Add(L("monitor.state_refreshing"));
        if (hasFailure) parts.Add(L(ErrorKeyFor(state.LastAttempt!.Error)));
        if (state.Stale) parts.Add(L("monitor.state_stale"));
        else if (snapshot is not null && !hasFailure && !state.Refreshing) parts.Add(L("monitor.state_ok"));
        if (state.PausedUntilUserRetry && state.Enabled) parts.Add(L("monitor.paused_short"));

        var updated = snapshot?.SucceededAtUtc is { } s ? L("monitor.updated", FormatTime(s)) : null;
        var statusBits = new List<string>();
        if (updated is not null) statusBits.Add(updated);
        statusBits.AddRange(parts);
        statusLabel.Text = statusBits.Count == 0 ? L("monitor.state_no_data_yet") : string.Join(" · ", statusBits);
        statusLabel.ForeColor = hasFailure ? BarRed : Color.FromArgb(142, 142, 147);

        badge.Text = snapshot?.Buckets.FirstOrDefault(b => b.Tier is not null)?.Tier ?? "";
        badge.Visible = badge.Text.Length > 0;
        // Position the badge after the (autosized) title once the text is known.
        badge.Left = title.Left + title.PreferredWidth + 8;

        rowsPanel.Controls.Clear();
        rowsPanel.Controls.OfType<Control>().ToList().ForEach(c => c.Dispose());

        var dimmed = state.Stale || hasFailure;

        if (!state.Enabled)
        {
            rowsPanel.Controls.Add(MakeRow(L("monitor.state_disabled_hint"), "", null, null, null, null, dimmed: true));
            SizeCard(card, rowsPanel, extraRows: 0);
            return;
        }

        if (snapshot is null)
        {
            rowsPanel.Controls.Add(MakeRow(
                hasFailure ? L(ErrorKeyFor(state.LastAttempt!.Error)) : L("monitor.state_no_data_yet"),
                "", null, null, null, null, dimmed: true));
            SizeCard(card, rowsPanel, extraRows: 0);
            return;
        }

        var windowKeyCounts = snapshot.Buckets
            .SelectMany(b => b.Windows)
            .GroupBy(w => w.SourceKey)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var bucket in snapshot.Buckets)
        {
            if (bucket.Error is not null)
            {
                rowsPanel.Controls.Add(MakeRow(bucket.DisplayName ?? bucket.SourceKey, "", null, null, null,
                    null, dimmed: true, note: L("monitor.bucket_error")));
                continue;
            }
            if (bucket.Subscribed == false)
            {
                rowsPanel.Controls.Add(MakeRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.no_subscription"),
                    null, null, null, null, dimmed: true));
                continue;
            }
            if (bucket.Windows.Count == 0)
            {
                rowsPanel.Controls.Add(MakeRow(bucket.DisplayName ?? bucket.SourceKey, L("monitor.not_returned"),
                    null, null, null, null, dimmed: true));
                continue;
            }

            foreach (var window in bucket.Windows)
            {
                var needsBucketPrefix = windowKeyCounts.GetValueOrDefault(window.SourceKey) > 1;
                var titleText = needsBucketPrefix
                    ? $"{ShortName(bucket.DisplayName ?? bucket.SourceKey)} {LocalizeWindowKey(window.SourceKey, window.Label)}"
                    : LocalizeWindowKey(window.SourceKey, window.Label);
                var badgeText = needsBucketPrefix ? "" : ShortName(bucket.DisplayName ?? bucket.SourceKey, maxLength: 4);

                double? fraction = null;
                Color? barColor = null;
                var percentText = "";

                if (!window.HasAnyQuotaField)
                {
                    percentText = L("monitor.not_provided");
                }
                else if (window.PercentOutOfRange || window.UsedPercent is < 0 or > 100)
                {
                    percentText = L("monitor.percent_out_of_range",
                        FormatPercent((window.UsedPercent ?? window.RemainingPercent) ?? 0));
                }
                else if (window.RemainingPercent is { } remaining)
                {
                    percentText = L("monitor.percent_remaining", FormatPercent(remaining));
                    fraction = Math.Clamp(remaining / 100.0, 0, 1);
                    barColor = remaining switch
                    {
                        <= ReminderEvaluator.Level2RemainingPercent => BarRed,
                        <= ReminderEvaluator.Level1RemainingPercent => BarAmber,
                        _ => BarGreen,
                    };
                }

                if (window.UsedText is not null && window.TotalText is not null)
                {
                    percentText += " · " + L("monitor.counts", window.UsedText, window.TotalText);
                }

                rowsPanel.Controls.Add(MakeRow(titleText, badgeText, percentText, fraction, barColor,
                    ResetText(window), dimmed));
            }
        }

        if (snapshot.ResetCredits is { } credits)
        {
            var detail = credits.Details is { Count: > 0 }
                ? DescribeCreditDetails(credits)
                : credits.Details is null ? L("monitor.reset_credits_count_only") : L("monitor.reset_credits_empty_details");
            rowsPanel.Controls.Add(MakeRow(
                L("monitor.reset_credits", credits.AvailableCount),
                L("monitor.badge.reset_credit"),
                null, null, null,
                detail, dimmed: false, note: credits.Details is { Count: > 0 } ? detail : null));
        }

        SizeCard(card, rowsPanel, extraRows: 0);
    }

    private static string ShortName(string name, int maxLength = 12) =>
        name.Length <= maxLength ? name : name[..(maxLength - 1)] + "…";

    private static string DescribeCreditDetails(ResetCreditSummary credits)
    {
        if (credits.Details is null) return L("monitor.reset_credits_count_only");
        if (credits.Details.Count == 0) return L("monitor.reset_credits_empty_details");
        return string.Join("; ", credits.Details.Select(c =>
        {
            var title = c.Title;
            if (c.ExpiresAtUtc is { } expires)
            {
                title += " " + L("monitor.reset_credit_expires", FormatTime(expires));
            }
            if (c.Status is { } status && status != "available")
            {
                title += $" [{status}]";
            }
            return title;
        }));
    }

    private Control MakeRow(string title, string badge, string? percentText, double? fraction,
        Color? barColor, string? rightText, bool dimmed, string? note = null)
    {
        var row = new Panel
        {
            AutoSize = true,
            Width = 348,
            Margin = new Padding(0, 4, 0, 4),
            BackColor = Color.White,
        };

        var fore = dimmed ? Color.FromArgb(142, 142, 147) : SystemColors.ControlText;

        var titleLabel = new Label
        {
            Text = title,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(0, 0),
            Size = new Size(128, 20),
            ForeColor = fore,
        };
        row.Controls.Add(titleLabel);

        if (badge.Length > 0)
        {
            var badgeLabel = new BadgeLabel
            {
                Text = badge,
                Location = new Point(Math.Min(titleLabel.PreferredWidth + 4, 130), 2),
                ForeColor = fore,
            };
            row.Controls.Add(badgeLabel);
        }

        var rightLabel = new Label
        {
            Text = rightText ?? "",
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            Size = new Size(120, 20),
            Location = new Point(228, 0),
            ForeColor = dimmed ? fore : Color.FromArgb(99, 99, 104),
        };
        row.Controls.Add(rightLabel);

        if (percentText is not null)
        {
            var percentLabel = new Label
            {
                Text = percentText,
                AutoSize = false,
                AutoEllipsis = true,
                Size = new Size(92, 20),
                Location = new Point(132, 0),
                ForeColor = fore,
            };
            row.Controls.Add(percentLabel);
        }

        if (fraction is { } f && barColor is { } color)
        {
            var bar = new QuotaBar
            {
                Fraction = f,
                FillColor = color,
                Size = new Size(224, 6),
                Location = new Point(0, 22),
                BackColor = Color.White,
            };
            row.Controls.Add(bar);
            row.Height = 34;
        }
        else
        {
            row.Height = 24;
        }

        if (note is not null)
        {
            var noteLabel = new Label
            {
                Text = note,
                AutoSize = false,
                AutoEllipsis = true,
                Size = new Size(348, 18),
                Location = new Point(0, row.Height - 2),
                ForeColor = Color.FromArgb(142, 142, 147),
                Font = new Font(Font.FontFamily, 8F),
            };
            row.Controls.Add(noteLabel);
            row.Height += 18;
        }

        return row;
    }

    private void SizeCard(Panel card, FlowLayoutPanel rowsPanel, int extraRows)
    {
        card.Height = rowsPanel.Location.Y + rowsPanel.Height + 12 + extraRows;
    }

    private static string ResetText(QuotaWindow window)
    {
        if (window.ResetsAtUtc is null) return "—";
        var local = window.ResetsAtUtc.Value.ToLocalTime();
        if (local <= DateTimeOffset.Now)
        {
            return L("monitor.reset_pending");
        }
        return $"{local:M/d HH:mm} " + L("monitor.reset_suffix");
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
        _memoryView.Dock = DockStyle.Fill;
        _memoryView.Padding = new Padding(10, 4, 10, 10);
        _memoryView.BackColor = PageBack;

        var card = new CardPanel
        {
            Location = new Point(10, 4),
            Size = new Size(372, 496),
            BackColor = Color.White,
            Padding = new Padding(12, 10, 12, 10),
        };

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 5,
            Location = new Point(12, 34),
            Size = new Size(348, 130),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        void AddStat(int column, int row, Label valueLabel)
        {
            valueLabel.AutoSize = false;
            valueLabel.Size = new Size(170, 22);
            valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            grid.Controls.Add(valueLabel, column, row);
        }

        AddStat(0, 0, _memoryPhysicalLabel);
        AddStat(0, 1, _memoryAvailableLabel);
        AddStat(0, 2, _memoryCommitLabel);
        AddStat(0, 3, _memorySignalLabel);
        AddStat(0, 4, _memorySampledLabel);

        _trendChart.Location = new Point(12, 172);
        _trendChart.Size = new Size(348, 258);
        _trendChart.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        _taskManagerButton.AutoSize = true;
        _taskManagerButton.FlatStyle = FlatStyle.Flat;
        _taskManagerButton.FlatAppearance.BorderSize = 0;
        _taskManagerButton.BackColor = Color.FromArgb(232, 232, 236);
        _taskManagerButton.Location = new Point(12, card.Height - 40);
        _taskManagerButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
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

        card.Controls.Add(_memoryStateLabel);
        _memoryStateLabel.AutoSize = true;
        _memoryStateLabel.Font = new Font(Font, FontStyle.Bold);
        _memoryStateLabel.Location = new Point(12, 10);
        card.Controls.Add(grid);
        card.Controls.Add(_trendChart);
        card.Controls.Add(_taskManagerButton);

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

        if (sample is not null)
        {
            _memoryPhysicalLabel.Text = L("monitor.memory_used_of",
                FormatBytes(sample.PhysicalUsedBytes), FormatBytes(sample.PhysicalTotalBytes));
            _memoryAvailableLabel.Text = L("monitor.memory_available", FormatBytes(sample.PhysicalAvailableBytes));
            _memoryCommitLabel.Text = L("monitor.memory_commit_values",
                FormatBytes(sample.CommitTotalBytes), FormatBytes(sample.CommitLimitBytes));
            _memorySignalLabel.Text = L("monitor.memory_low_signal") + "：" + sample.LowMemorySignal switch
            {
                true => L("monitor.low_triggered"),
                false => L("monitor.low_not_triggered"),
                null => L("monitor.low_unknown"),
            };
            _memorySampledLabel.Text = L("monitor.memory_sampled", FormatTime(sample.SampledAtUtc));
        }
        else
        {
            _memoryPhysicalLabel.Text = L("monitor.not_provided");
            _memoryAvailableLabel.Text = L("monitor.not_provided");
            _memoryCommitLabel.Text = L("monitor.not_provided");
            _memorySignalLabel.Text = L("monitor.memory_low_signal") + "：" + L("monitor.low_unknown");
            _memorySampledLabel.Text = L("monitor.memory_sampled", "—");
        }

        _trendChart.Samples = _coordinator.MemoryHistory.Snapshot();
        _trendChart.Invalidate();
    }

    // ---------- settings view ----------

    private void BuildSettingsView()
    {
        _settingsView.Dock = DockStyle.Fill;
        _settingsView.Padding = new Padding(10, 4, 10, 10);
        _settingsView.BackColor = PageBack;
        _settingsView.AutoScroll = true;

        var card = new CardPanel
        {
            Location = new Point(10, 4),
            Size = new Size(372, 496),
            BackColor = Color.White,
            Padding = new Padding(12, 10, 12, 10),
        };

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Location = new Point(6, 6),
            Size = new Size(352, 480),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var enable = new CheckBox
            {
                Name = $"enable_{id}",
                AutoSize = true,
                Text = L($"monitor.provider.{id.ToString().ToLowerInvariant()}"),
                Margin = new Padding(2, 6, 2, 0),
            };
            var path = new TextBox
            {
                Name = $"cliPath_{id}",
                Width = 330,
                Margin = new Padding(20, 0, 2, 8),
            };
            _toolTip.SetToolTip(path, L("monitor.settings.cli_path"));
            _enableChecks[id] = enable;
            _pathBoxes[id] = path;
            layout.Controls.Add(enable);
            layout.Controls.Add(path);
        }

        _memoryCheck.Name = "memoryEnabledCheck";
        _memoryCheck.AutoSize = true;
        _memoryCheck.Text = L("monitor.settings.memory");
        _memoryCheck.Margin = new Padding(2, 10, 2, 0);
        _remindersCheck.Name = "remindersCheck";
        _remindersCheck.AutoSize = true;
        _remindersCheck.Text = L("monitor.settings.reminders");
        _remindersCheck.Margin = new Padding(2, 6, 2, 0);

        _hotkeyHint.AutoSize = false;
        _hotkeyHint.Size = new Size(336, 34);
        _hotkeyHint.ForeColor = Color.FromArgb(142, 142, 147);
        _hotkeyHint.Margin = new Padding(2, 10, 2, 0);

        _saveButton.AutoSize = true;
        _saveButton.FlatStyle = FlatStyle.Flat;
        _saveButton.FlatAppearance.BorderSize = 0;
        _saveButton.BackColor = Accent;
        _saveButton.ForeColor = Color.White;
        _saveButton.Padding = new Padding(10, 0, 10, 0);
        _saveButton.Margin = new Padding(2, 10, 2, 0);
        _saveButton.Click += (_, _) => ApplySettingsFromView();

        _saveStatusLabel.AutoSize = true;
        _saveStatusLabel.ForeColor = Color.FromArgb(142, 142, 147);
        _saveStatusLabel.Margin = new Padding(8, 14, 2, 0);

        layout.Controls.Add(_memoryCheck);
        layout.Controls.Add(_remindersCheck);
        layout.Controls.Add(_hotkeyHint);
        layout.Controls.Add(_saveButton);
        layout.Controls.Add(_saveStatusLabel);

        card.Controls.Add(layout);
        _settingsView.Controls.Add(card);
    }

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
        _hotkeyHint.Text = L("monitor.hotkey_hint", settings.PopoverHotkey);
        _saveStatusLabel.Text = "";
    }

    private void ApplySettingsFromView()
    {
        var settings = _coordinator.Settings with
        {
            MemoryEnabled = _memoryCheck.Checked,
            RemindersEnabled = _remindersCheck.Checked,
            Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
            {
                Id = id,
                Enabled = _enableChecks[id].Checked,
                CliPath = string.IsNullOrWhiteSpace(_pathBoxes[id].Text) ? null : _pathBoxes[id].Text.Trim(),
            }).ToList(),
        };

        // Apply first so the views update even if the save fails (a failed save is
        // reported, never faked — spec §8(6)).
        _coordinator.ApplySettings(settings);
        var saved = _coordinator.SaveSettings(settings);
        _saveStatusLabel.Text = saved ? L("monitor.saved") : L("monitor.settings.save_failed");
        _hotkeyHint.Text = L("monitor.hotkey_hint", settings.PopoverHotkey);
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

    internal static string FormatPercent(double value) =>
        value == Math.Floor(value) ? ((int)value).ToString() : value.ToString("0.0");

    internal static string FormatBytes(ulong bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        const double mb = 1024.0 * 1024;
        return bytes >= (ulong)gb
            ? (bytes / gb).ToString("0.#") + " GB"
            : (bytes / mb).ToString("0") + " MB";
    }

    internal static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

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
            var track = new Rectangle(0, Height / 2 - 3, Width, 6);
            using var trackBrush = new SolidBrush(Color.FromArgb(235, 235, 240));
            using var trackPath = RoundedPath(track, 3);
            g.FillPath(trackBrush, trackPath);

            var fillWidth = (int)Math.Round(Width * Math.Clamp(Fraction, 0, 1));
            if (fillWidth > 0)
            {
                var fill = new Rectangle(0, Height / 2 - 3, Math.Max(fillWidth, 6), 6);
                using var fillBrush = new SolidBrush(FillColor);
                using var fillPath = RoundedPath(fill, 3);
                g.FillPath(fillBrush, fillPath);
            }
        }
    }

    /// <summary>Small rounded badge text (tier names like pro / personal, plan labels).</summary>
    internal sealed class BadgeLabel : Control
    {
        public BadgeLabel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            ForeColor = Color.FromArgb(99, 99, 104);
            Size = new Size(36, 18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Text.Length == 0) return;
            var textSize = TextRenderer.MeasureText(Text, Font);
            var rect = new Rectangle(0, 0, textSize.Width + 10, Height - 1);
            using var brush = new SolidBrush(Color.FromArgb(240, 240, 243));
            using var path = RoundedPath(rect, rect.Height / 2);
            g.FillPath(brush, path);
            TextRenderer.DrawText(g, Text, Font, rect, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            var textSize = TextRenderer.MeasureText(Text, Font);
            Width = Math.Max(20, textSize.Width + 12);
            Invalidate();
        }
    }

    /// <summary>White rounded card with a hairline border (provider sections / view pages).</summary>
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 10);
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, path);
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// Draws the one-hour trend as TWO stacked mini-charts — physical available and commit —
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

        public MemoryTrendChart()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
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
            double min = double.MaxValue, max = double.MinValue;
            foreach (var s in Samples)
            {
                if (s.SampledAtUtc < windowStart) continue;
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

            using var linePen = new Pen(Accent, 1.6f);
            var previous = default(MemorySample);
            foreach (var s in Samples)
            {
                if (s.SampledAtUtc < windowStart)
                {
                    previous = s;
                    continue;
                }
                if (previous is not null &&
                    (s.SampledAtUtc - previous.SampledAtUtc).TotalSeconds > GapBreakThresholdSeconds)
                {
                    // 缺测断点：不连接，不插值 (spec §6).
                    previous = null;
                }
                if (previous is not null)
                {
                    g.DrawLine(linePen, X(previous.SampledAtUtc), Y(value(previous)), X(s.SampledAtUtc), Y(value(s)));
                }
                previous = s;
            }

            // Axis labels: min/max of THIS series only, in bytes — never a shared unitless axis.
            g.DrawString(FormatBytes((ulong)max), Font, SystemBrushes.ControlText, 2, top - 2);
            g.DrawString(FormatBytes((ulong)Math.Max(0, min)), Font, SystemBrushes.ControlText, 2, bounds.Bottom - 14);
            g.DrawString(name, Font, SystemBrushes.ControlText, bounds.Left + 4, top + 2);
        }
    }
}
