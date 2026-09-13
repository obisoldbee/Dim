using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;

namespace OBDim.UI;

/// <summary>
/// The "Usage &amp; Memory" panel (spec §4): one instance at a time, opened from the tray
/// context menu, closed with Esc or the title bar. Closing the form does NOT stop the
/// coordinator — it keeps sampling and refreshing in the background; only app exit does.
/// The panel is a plain WinForms tool window: information hierarchy per spec §4.2, no
/// custom chrome, system DPI scaling, keyboard accessible.
/// </summary>
public sealed class MonitorForm : Form
{
    private readonly MonitoringCoordinator _coordinator;

    private static string L(string key, params object[] args) => LocalizationService.Get(key, args);

    private readonly TabControl _tabs = new();
    private readonly TabPage _quotaTab = new();
    private readonly TabPage _memoryTab = new();
    private readonly Button _refreshAllButton = new();
    private readonly Button _settingsButton = new();

    private readonly FlowLayoutPanel _quotaPanel = new();
    private readonly Dictionary<ProviderId, GroupBox> _providerGroups = [];
    private readonly Dictionary<ProviderId, Label> _providerStatusLabels = [];
    private readonly Dictionary<ProviderId, ListView> _providerLists = [];
    private readonly Dictionary<ProviderId, Label> _providerExtraLabels = [];

    // Memory tab controls
    private readonly Label _memoryPhysicalLabel = new();
    private readonly Label _memoryAvailableLabel = new();
    private readonly Label _memoryCommitLabel = new();
    private readonly Label _memorySignalLabel = new();
    private readonly Label _memorySampledLabel = new();
    private readonly Label _memoryStateLabel = new();
    private readonly MemoryTrendChart _trendChart = new();

    public MonitorForm(MonitoringCoordinator coordinator)
    {
        _coordinator = coordinator;

        Text = L("monitor.title");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);

        ClientSize = new Size(560, 640);

        BuildLayout();
        ApplyLocalization();

        _coordinator.QuotaStateChanged += OnCoordinatorChanged;
        _coordinator.MemoryStateChanged += OnCoordinatorChanged;
        _coordinator.ReminderFired += OnReminderFired;

        FormClosed += (_, _) =>
        {
            _coordinator.QuotaStateChanged -= OnCoordinatorChanged;
            _coordinator.MemoryStateChanged -= OnCoordinatorChanged;
            _coordinator.ReminderFired -= OnReminderFired;
        };

        RefreshQuotaTab();
        RefreshMemoryTab();
    }

    private static string L_single(string key) => LocalizationService.Get(key);

    private void BuildLayout()
    {
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 8, 8, 0),
            WrapContents = false,
        };
        _refreshAllButton.Name = "refreshAllButton";
        _refreshAllButton.AutoSize = true;
        _refreshAllButton.Click += (_, _) => _coordinator.RequestManualRefreshAll();
        _settingsButton.Name = "monitorSettingsButton";
        _settingsButton.AutoSize = true;
        _settingsButton.Click += (_, _) => OpenMonitoringSettings();
        header.Controls.Add(_refreshAllButton);
        header.Controls.Add(_settingsButton);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Name = "monitorTabs";
        _quotaTab.Name = "quotaTab";
        _memoryTab.Name = "memoryTab";
        _tabs.TabPages.Add(_quotaTab);
        _tabs.TabPages.Add(_memoryTab);

        BuildQuotaTab();
        BuildMemoryTab();

        Controls.Add(_tabs);
        Controls.Add(header);
    }

    private void BuildQuotaTab()
    {
        _quotaPanel.Dock = DockStyle.Fill;
        _quotaPanel.FlowDirection = FlowDirection.TopDown;
        _quotaPanel.WrapContents = false;
        _quotaPanel.AutoScroll = true;
        _quotaPanel.Padding = new Padding(8);

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var group = new GroupBox
            {
                Name = $"providerGroup_{id}",
                Width = 520,
                Height = 150,
                Padding = new Padding(8),
            };

            var status = new Label
            {
                Name = $"providerStatus_{id}",
                AutoSize = true,
                Location = new Point(10, 22),
                MaximumSize = new Size(490, 0),
            };

            var list = new ListView
            {
                Name = $"providerList_{id}",
                View = View.Details,
                FullRowSelect = true,
                HideSelection = true,
                Location = new Point(10, 46),
                Size = new Size(490, 72),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            list.Columns.Add("window", 220);
            list.Columns.Add("quota", 160);
            list.Columns.Add("reset", 108);

            var extra = new Label
            {
                Name = $"providerExtra_{id}",
                AutoSize = true,
                Location = new Point(10, 122),
                MaximumSize = new Size(490, 0),
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };

            group.Controls.Add(status);
            group.Controls.Add(list);
            group.Controls.Add(extra);

            _providerGroups[id] = group;
            _providerStatusLabels[id] = status;
            _providerLists[id] = list;
            _providerExtraLabels[id] = extra;
            _quotaPanel.Controls.Add(group);
        }

        _quotaTab.Controls.Add(_quotaPanel);
    }

    private void BuildMemoryTab()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(10),
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(Control left, Control right, bool fill = false)
        {
            var row = layout.RowCount;
            layout.RowStyles.Add(new RowStyle(fill ? SizeType.Percent : SizeType.AutoSize, fill ? 100 : 0));
            left.AutoSize = true;
            right.AutoSize = !fill;
            right.Anchor = AnchorStyles.Left;
            layout.Controls.Add(left, 0, row);
            layout.Controls.Add(right, 1, row);
        }

        _memoryPhysicalLabel.Name = "memoryPhysicalLabel";
        _memoryAvailableLabel.Name = "memoryAvailableLabel";
        _memoryCommitLabel.Name = "memoryCommitLabel";
        _memorySignalLabel.Name = "memorySignalLabel";
        _memorySampledLabel.Name = "memorySampledLabel";
        _memoryStateLabel.Name = "memoryStateLabel";

        AddRow(new Label { Text = L("monitor.memory_physical"), AutoSize = true }, _memoryPhysicalLabel);
        AddRow(new Label { AutoSize = true }, _memoryAvailableLabel);
        AddRow(new Label { Text = L("monitor.memory_commit"), AutoSize = true }, _memoryCommitLabel);
        AddRow(new Label { Text = L("monitor.memory_low_signal"), AutoSize = true }, _memorySignalLabel);
        AddRow(new Label { Text = L("monitor.memory_sampled_label"), AutoSize = true }, _memorySampledLabel);
        AddRow(new Label { Text = L("monitor.memory_state_label"), AutoSize = true }, _memoryStateLabel);

        _trendChart.Dock = DockStyle.Fill;
        _trendChart.MinimumSize = new Size(500, 260);

        var host = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(layout, 0, 0);
        host.Controls.Add(_trendChart, 0, 1);
        _memoryTab.Controls.Add(host);
    }

    private void OpenMonitoringSettings()
    {
        using var form = new MonitoringSettingsForm(_coordinator);
        form.ShowDialog(this);
    }

    private void OnCoordinatorChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            RefreshQuotaTab();
            RefreshMemoryTab();
        });
    }

    private void OnReminderFired(QuotaReminderEvent evt)
    {
        // Reminders surface as tray balloons (owned by TrayApp); the panel just refreshes.
        if (IsDisposed) return;
        BeginInvoke(RefreshQuotaTab);
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

    /// <summary>Places the window inside the working area of the screen the cursor is on.</summary>
    public void PlaceNearCursor()
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Min(Cursor.Position.X + 12, screen.Right - Width);
        var y = Math.Min(Cursor.Position.Y + 12, screen.Bottom - Height);
        Location = new Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));
    }

    private void RefreshQuotaTab()
    {
        var states = _coordinator.GetDisplayStates();
        foreach (var state in states)
        {
            UpdateProviderGroup(state);
        }
    }

    private void UpdateProviderGroup(ProviderDisplayState state)
    {
        var group = _providerGroups[state.Provider];
        var status = _providerStatusLabels[state.Provider];
        var list = _providerLists[state.Provider];
        var extra = _providerExtraLabels[state.Provider];

        group.Text = L($"monitor.provider.{state.Provider.ToString().ToLowerInvariant()}");

        // Status is multi-dimensional on purpose (spec §5.3): refresh state, failure and
        // freshness are independent facts and can all be true at once.
        var parts = new List<string>();
        if (!state.Enabled)
        {
            parts.Add(L("monitor.state_disabled"));
        }
        else if (state.Refreshing)
        {
            parts.Add(L("monitor.state_refreshing"));
        }

        var snapshot = state.LastGood;
        var hasFailure = state.LastAttempt is { HasError: true };
        if (hasFailure)
        {
            var attempt = state.LastAttempt!;
            var errorText = L(ErrorKeyFor(attempt.Error));
            parts.Add($"{L("monitor.state_error")} · {errorText}");
        }

        if (state.Stale)
        {
            parts.Add(L("monitor.state_stale"));
        }
        else if (snapshot is not null && !hasFailure)
        {
            parts.Add(snapshot.IsPartial ? L("monitor.state_partial") : L("monitor.state_ok"));
        }

        if (state.PausedUntilUserRetry && state.Enabled)
        {
            parts.Add(L("monitor.paused"));
        }

        var successPart = snapshot?.SucceededAtUtc is { } success
            ? L("monitor.last_success", FormatTime(success))
            : null;
        status.Text = parts.Count == 0
            ? L("monitor.state_no_data_yet")
            : string.Join(" · ", parts) + (successPart is null ? "" : $" · {successPart}");

        list.BeginUpdate();
        list.Items.Clear();

        if (!state.Enabled)
        {
            AddSimpleRow(list, L("monitor.state_disabled"), "", "");
        }
        else if (snapshot is null)
        {
            AddSimpleRow(list,
                hasFailure ? L("monitor.state_error") : L("monitor.state_no_data_yet"),
                hasFailure ? L(ErrorKeyFor(state.LastAttempt!.Error)) : "",
                "");
        }
        else
        {
            FillSnapshotRows(list, snapshot);
        }
        list.EndUpdate();

        extra.Text = BuildExtraText(state);
        extra.Visible = extra.Text.Length > 0;
        list.Height = Math.Max(50, list.Items.Count * 19 + 26);
        extra.Location = new Point(10, list.Bottom + 4);
        group.Height = (extra.Visible ? extra.Bottom + 10 : list.Bottom + 12);
    }

    private void FillSnapshotRows(ListView list, ProviderSnapshot snapshot)
    {
        var anyRow = false;
        foreach (var bucket in snapshot.Buckets)
        {
            if (bucket.Error is not null)
            {
                AddSimpleRow(list, bucket.DisplayName ?? bucket.SourceKey, L("monitor.bucket_error"), "");
                anyRow = true;
                continue;
            }

            if (bucket.Subscribed == false)
            {
                AddSimpleRow(list, bucket.DisplayName ?? bucket.SourceKey, L("monitor.no_subscription"), "");
                anyRow = true;
                continue;
            }

            if (bucket.Windows.Count == 0)
            {
                AddSimpleRow(list, bucket.DisplayName ?? bucket.SourceKey, L("monitor.not_returned"), "");
                anyRow = true;
                continue;
            }

            foreach (var window in bucket.Windows)
            {
                list.Items.Add(new ListViewItem([
                    RowTitle(bucket, window),
                    QuotaText(window),
                    ResetText(window),
                ]));
                anyRow = true;
            }
        }

        if (!anyRow)
        {
            AddSimpleRow(list, L("monitor.not_returned"), "", "");
        }

        if (snapshot.ResetCredits is { } credits)
        {
            AddSimpleRow(list, L("monitor.reset_credits", credits.AvailableCount),
                DescribeCreditDetails(credits), "");
        }
    }

    private static void AddSimpleRow(ListView list, string window, string quota, string reset)
    {
        list.Items.Add(new ListViewItem([window, quota, reset]));
    }

    private static string RowTitle(QuotaBucket bucket, QuotaWindow window)
    {
        var bucketName = bucket.DisplayName ?? bucket.SourceKey;
        var windowName = LocalizeWindowKey(window.SourceKey, window.Label);
        var duration = DescribeDuration(window.WindowDurationMinutes);
        var title = $"{bucketName} · {windowName}";
        if (bucket.SeatId is not null) title += $" · {bucket.SeatId}";
        if (duration is not null) title += $" ({duration})";
        return title;
    }

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

    private static string? DescribeDuration(long? minutes)
    {
        if (minutes is null or <= 0) return null;
        if (minutes % 1440 == 0) return LocalizationService.Get("common.duration_days", minutes / 1440);
        if (minutes % 60 == 0) return LocalizationService.Get("common.duration_hours", minutes / 60);
        return LocalizationService.Get("common.duration_minutes", minutes.Value);
    }

    private static string QuotaText(QuotaWindow window)
    {
        if (!window.HasAnyQuotaField) return LocalizationService.Get("monitor.not_provided");
        if (window.PercentOutOfRange || window.UsedPercent is < 0 or > 100)
        {
            return LocalizationService.Get("monitor.percent_out_of_range",
                FormatPercent((window.UsedPercent ?? window.RemainingPercent) ?? 0));
        }

        var parts = new List<string>();
        if (window.UsedPercent is { } used && window.RemainingPercent is { } remaining)
        {
            parts.Add(LocalizationService.Get("monitor.percent_used",
                FormatPercent(used), FormatPercent(remaining)));
        }
        if (window.UsedText is not null && window.TotalText is not null)
        {
            parts.Add(LocalizationService.Get("monitor.counts", window.UsedText, window.TotalText));
        }
        return parts.Count == 0 ? LocalizationService.Get("monitor.not_provided") : string.Join(" · ", parts);
    }

    internal static string FormatPercent(double value) =>
        value == Math.Floor(value) ? ((int)value).ToString() : value.ToString("0.0");

    private static string ResetText(QuotaWindow window)
    {
        if (window.ResetsAtUtc is null) return LocalizationService.Get("monitor.reset_unknown");
        var local = window.ResetsAtUtc.Value.ToLocalTime();
        if (local <= DateTimeOffset.Now)
        {
            return LocalizationService.Get("monitor.reset_pending");
        }
        return FormatTime(local) + " (" + FormatCountdown(local - DateTimeOffset.Now) + ")";
    }

    private static string DescribeCreditDetails(ResetCreditSummary credits)
    {
        if (credits.Details is null)
        {
            return LocalizationService.Get("monitor.reset_credits_count_only");
        }
        if (credits.Details.Count == 0)
        {
            return LocalizationService.Get("monitor.reset_credits_empty_details");
        }
        return string.Join("; ", credits.Details.Select(c =>
        {
            var title = c.Title;
            if (c.ExpiresAtUtc is { } expires)
            {
                title += " " + LocalizationService.Get("monitor.reset_credit_expires", FormatTime(expires));
            }
            if (c.Status is { } status && status != "available")
            {
                title += $" [{status}]";
            }
            return title;
        }));
    }

    private string BuildExtraText(ProviderDisplayState state)
    {
        var snapshot = state.LastGood;
        var bits = new List<string>();
        if (snapshot?.IdentityDisplay is { } identity) bits.Add(identity);
        if (snapshot?.CliVersion is { } version) bits.Add($"CLI {version}");
        return string.Join(" · ", bits);
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

    private void RefreshMemoryTab()
    {
        var settings = _coordinator.Settings;
        var sample = _coordinator.LatestMemorySample;
        var error = _coordinator.LastMemoryError;
        var stale = _coordinator.MemoryStale;

        if (!settings.MemoryEnabled)
        {
            _memoryStateLabel.Text = L("monitor.memory_disabled");
        }
        else if (sample is null)
        {
            _memoryStateLabel.Text = error is null
                ? L("monitor.memory_no_data")
                : L("monitor.memory_error", error);
        }
        else if (stale)
        {
            _memoryStateLabel.Text = L("monitor.memory_stale");
        }
        else
        {
            _memoryStateLabel.Text = L("monitor.memory_live");
        }

        if (sample is not null)
        {
            _memoryPhysicalLabel.Text = L("monitor.memory_used_of",
                FormatBytes(sample.PhysicalUsedBytes), FormatBytes(sample.PhysicalTotalBytes));
            _memoryAvailableLabel.Text = L("monitor.memory_available", FormatBytes(sample.PhysicalAvailableBytes));
            _memoryCommitLabel.Text = L("monitor.memory_commit_values",
                FormatBytes(sample.CommitTotalBytes), FormatBytes(sample.CommitLimitBytes));
            _memorySignalLabel.Text = sample.LowMemorySignal switch
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
            _memorySignalLabel.Text = L("monitor.low_unknown");
            _memorySampledLabel.Text = L("monitor.memory_sampled", "—");
        }

        _trendChart.Samples = _coordinator.MemoryHistory.Snapshot();
        _trendChart.Invalidate();
    }

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

    private void ApplyLocalization()
    {
        _refreshAllButton.Text = L("monitor.refresh_all");
        _settingsButton.Text = L("monitor.settings");
        _quotaTab.Text = L("monitor.tab_quota");
        _memoryTab.Text = L("monitor.tab_memory");
        foreach (var list in _providerLists.Values)
        {
            list.Columns[0].Text = L("monitor.col_window");
            list.Columns[1].Text = L("monitor.col_quota");
            list.Columns[2].Text = L("monitor.col_reset");
        }
        _trendChart.Title = L("monitor.trend_title");
        _trendChart.SeriesNames = (L("monitor.trend_physical"), L("monitor.trend_commit"));
        _trendChart.EmptyText = L("monitor.trend_no_data");
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
            BackColor = SystemColors.Window;
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
            using var borderPen = new Pen(SystemColors.ControlDark);
            g.DrawRectangle(borderPen, bounds);

            // One hour window ending "now"; x positions are time-mapped so gaps show.
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

            using var linePen = new Pen(Color.FromArgb(70, 110, 180), 1.6f);
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
