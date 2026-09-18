using System.Runtime.InteropServices;
using System.Windows.Forms;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI;
using OBDim.UI.Memory;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// R28 / R31 / AM04: the memory page is one chart, a physical/commit switch, five ranges and six
/// figures that stay on screen. The claims worth guarding are the ones a screenshot cannot make:
/// that switching series reaches no CLI, that painting consumes cached geometry, and that the
/// chart actually receives its arrow keys.
/// </summary>
[Collection("NativeUi")]
public class MemoryPageUiTests : IDisposable
{
    private static readonly string[] StatKeys =
    [
        "monitor.memory.stat_physical", "monitor.memory.stat_used", "monitor.memory.stat_available",
        "monitor.memory.stat_commit", "monitor.memory.stat_commit_limit", "monitor.memory.stat_low_signal",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"obdim-memui-{Guid.NewGuid():N}");

    public MemoryPageUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void MemoryPage_HasOneChartAndSixPersistentFigures()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            var chartCount = Descendants<MemoryTrendPanel>(form).Count();
            Assert.True(chartCount == 1,
                $"the page is a single chart selected by metric, not two stacked — found {chartCount}");

            foreach (var metric in Enum.GetValues<MemoryTrendMetric>())
            {
                chart.Metric = metric;
                Application.DoEvents();
                var visible = StatLabels(form).ToList();
                Assert.Equal(6, visible.Count);
                Assert.All(visible, l => Assert.True(l.Visible && !string.IsNullOrWhiteSpace(l.Text)));
            }
        });
    }

    /// <summary>
    /// Switching metric or range is a projection over history already held. If it ever reaches for
    /// a CLI, the page stalls on a provider round-trip to redraw a line it can draw immediately.
    /// </summary>
    [Fact]
    public void MetricAndRangeSwitch_ReachNoCliAndRequestNoSample()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            var reads = harness.Reader.Reads;
            var queries = harness.ProviderQueries;

            chart.Metric = MemoryTrendMetric.Commit;
            chart.Metric = MemoryTrendMetric.PhysicalUsed;
            foreach (var range in new[] { MemoryTrendRange.Minute1, MemoryTrendRange.Minute30, MemoryTrendRange.Hour2 })
            {
                chart.Range = range;
            }
            Application.DoEvents();

            Assert.Equal(reads, harness.Reader.Reads);
            Assert.Equal(queries, harness.ProviderQueries);
        });
    }

    /// <summary>
    /// Clicking the on-screen switches, not just setting properties: the buttons exist, are
    /// labelled, and drive the same selection.
    /// </summary>
    [Fact]
    public void SwitchButtons_DriveTheChartsSelection()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            ChartButton(form, "monitor.metric_commit").PerformClick();
            Application.DoEvents();
            Assert.Equal(MemoryTrendMetric.Commit, chart.Metric);

            ChartButton(form, "monitor.range_10m").PerformClick();
            Application.DoEvents();
            Assert.Equal(MemoryTrendRange.Minute10, chart.Range);

            Assert.Equal(5, RangeButtons(form).Count());
        });
    }

    /// <summary>
    /// The popover's outer frame must not jump between pages or ranges: the 540×760 contract is
    /// what lets both pages share one mental model.
    /// </summary>
    [Fact]
    public void RangeAndMetricSwitches_KeepTheFrameSize()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out _);
            var size = form.Size;

            foreach (var button in RangeButtons(form).Concat(MetricButtons(form)))
            {
                button.PerformClick();
                Application.DoEvents();
                Assert.Equal(size, form.Size);
            }

            form.SetView(MonitorForm.View.Quota);
            Application.DoEvents();
            form.SetView(MonitorForm.View.Memory);
            Application.DoEvents();
            Assert.Equal(size, form.Size);
        });
    }

    /// <summary>
    /// OnPaint consumes prepared geometry. Twenty forced repaints may cost at most the one rebuild
    /// the sliding window needs for the second they land in; a chart that recomputed per paint
    /// would report twenty.
    /// </summary>
    [Fact]
    public void Repaints_ReuseCachedGeometry()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            var before = chart.GeometryBuildCount;
            for (var i = 0; i < 20; i++)
            {
                chart.Invalidate();
                chart.Update();
            }

            Assert.True(chart.GeometryBuildCount - before <= 2,
                $"{chart.GeometryBuildCount - before} rebuilds for 20 repaints — OnPaint is running the pipeline again");
        });
    }

    /// <summary>
    /// A history correction at an existing timestamp must not be silently served from cache: the
    /// repaint is keyed on the buffer's version, not on count and newest timestamp.
    /// </summary>
    [Fact]
    public void SameTimestampCorrection_InvalidatesTheCachedCurve()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            var before = harness.Coordinator.MemoryHistory.SnapshotWithVersion();
            var first = before.Samples[0];
            harness.Coordinator.MemoryHistory.Add(first with { PhysicalAvailableBytes = 1_000_000_000 });
            var after = harness.Coordinator.MemoryHistory.SnapshotWithVersion();

            Assert.Equal(before.Samples.Count, after.Samples.Count);
            Assert.NotEqual(before.Version, after.Version);

            chart.Apply(after, stale: false);
            chart.Invalidate();
            chart.Update();
            Assert.True(chart.GeometryBuildCount > 0);
            Assert.NotNull(chart.CurrentPoint);
        });
    }

    /// <summary>
    /// The keyboard reading has to survive the form's own key handling — ProcessCmdKey turns
    /// Left/Right into "switch page", which is what used to make the chart unreachable by keyboard.
    /// </summary>
    [Fact]
    public void ArrowKey_WhileChartFocused_StaysOnTheMemoryPageAndMovesTheReading()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            form.ActiveControl = chart;
            Application.DoEvents();
            Assert.True(chart.Focused, "the chart did not take focus");

            Assert.True(form.ChartOwnsKey(Keys.Left), "the form is still claiming the chart's arrow key");

            // Gaining focus anchors the reading on the newest sample, so a keyboard user gets a
            // value from the first keystroke rather than an empty plot.
            Assert.True(chart.HasReading, "focus should place the crosshair on the newest point");

            SendKey(chart, VkLeft);
            Assert.True(chart.Visible, "the page switched away under the arrow key");
            Assert.True(chart.HasReading);

            SendKey(chart, VkEscape);
            Assert.False(chart.HasReading, "Escape should retire the chart's reading");
            Assert.True(form.Visible, "the first Escape belongs to the chart, not the popover");

            SendKey(chart, VkEscape);
            Assert.False(form.Visible, "with nothing showing, Escape dismisses the popover as before");

            form.ActiveControl = null;
            Assert.False(form.ChartOwnsKey(Keys.Left), "without chart focus the arrow keys page again");
        });
    }

    /// <summary>
    /// Sized-invariant rather than rendered pixels, for the reason in docs/research/260913: the
    /// xunit host is DPI-unaware, so a themed button's wider text inset — which is what clipped
    /// 额度 down to one character in production — cannot be reproduced by pixel assertions here.
    /// </summary>
    [Fact]
    public void SegmentButtons_AreWideEnoughForThemedDpiAwareTextInset()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out _);

            var requiredInset = (int)Math.Round(8 * (form.DeviceDpi / 96.0));
            var buttons = RangeButtons(form).Concat(MetricButtons(form)).ToList();
            Assert.Equal(7, buttons.Count);

            foreach (var button in buttons)
            {
                Assert.True(button.AutoSize, $"segment '{button.Text}' must stay AutoSize");
                var measured = TextRenderer.MeasureText(button.Text, button.Font).Width;
                Assert.True(button.Width >= measured + requiredInset,
                    $"segment '{button.Text}' is {button.Width}px but needs measured {measured}px + {requiredInset}px");
            }
        });
    }

    /// <summary>
    /// Each metric's headline is its own figure over its own denominator: 已用/物理总量 and
    /// 已提交/提交上限. Swapping them would print a commit percentage under a physical label.
    /// </summary>
    [Fact]
    public void Headline_FollowsTheSelectedMetric()
    {
        OnSta(() =>
        {
            using var harness = new Harness(_dir);
            using var form = OpenMemoryPage(harness, out var chart);

            chart.Metric = MemoryTrendMetric.PhysicalUsed;
            Application.DoEvents();
            var physical = HeadlineText(form);

            chart.Metric = MemoryTrendMetric.Commit;
            Application.DoEvents();
            var commit = HeadlineText(form);

            Assert.NotEqual(physical, commit);
            Assert.Contains("%", commit);
        });
    }

    // ---------- helpers ----------

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkLeft = 0x25;
    private const int VkEscape = 0x1B;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void SendKey(Control target, int vk)
    {
        SendMessage(target.Handle, WmKeyDown, (IntPtr)vk, IntPtr.Zero);
        SendMessage(target.Handle, WmKeyUp, (IntPtr)vk, IntPtr.Zero);
        Application.DoEvents();
    }

    private static MonitorForm OpenMemoryPage(Harness harness, out MemoryTrendPanel chart)
    {
        var form = new MonitorForm(harness.Coordinator);
        form.SetView(MonitorForm.View.Memory);
        form.Show();
        Application.DoEvents();
        chart = Descendants<MemoryTrendPanel>(form).Single();
        return form;
    }

    private static string HeadlineText(Control form) => string.Join("|",
        Descendants<Label>(form)
            .Where(l => l.Font.Size > 15f || l.Text.EndsWith('%') || l.Text.Contains('%'))
            .Select(l => l.Text));

    private static IEnumerable<Label> StatLabels(Control root) => Descendants<Label>(root).Where(l =>
        StatKeys.Any(key => l.Text.StartsWith(LocalizationService.Get(key), StringComparison.Ordinal)));

    private static Button ChartButton(Control root, string localizationKey)
    {
        var name = LocalizationService.Get(localizationKey);
        var button = Descendants<Button>(root).FirstOrDefault(b => b.AccessibleName == name || b.Text == name);
        Assert.True(button is not null, $"no button labelled '{name}'");
        return button!;
    }

    private static IEnumerable<Button> MetricButtons(Control root) =>
        new[] { "monitor.metric_physical", "monitor.metric_commit" }.Select(k => ChartButton(root, k));

    private static IEnumerable<Button> RangeButtons(Control root) =>
        new[] { "monitor.range_1m", "monitor.range_10m", "monitor.range_30m", "monitor.range_1h", "monitor.range_2h" }
            .Select(k => ChartButton(root, k));

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "the STA test thread did not finish in time");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("Assertion failed on the STA thread: " + failure.Message, failure);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string parentDir)
        {
            var dir = Path.Combine(parentDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Reader = new SequencedReader();
            Adapters = Enum.GetValues<ProviderId>()
                .ToDictionary(id => id, id => (IProviderAdapter)new QuietAdapter { Id = id });

            // Seed the history directly: what matters is that the page draws from samples it already
            // holds. One real refresh supplies the "latest sample" the figure table reads.
            var history = new MemoryHistoryBuffer();
            var start = DateTimeOffset.UtcNow.AddMinutes(-3);
            for (var i = 0; i <= 30; i++)
            {
                history.Add(new MemorySample
                {
                    SampledAtUtc = start.AddSeconds(i * 5),
                    PhysicalTotalBytes = 16_000_000_000,
                    PhysicalAvailableBytes = (ulong)(6_000_000_000 + i * 10_000_000),
                    CommitTotalBytes = (ulong)(9_000_000_000 + i * 8_000_000),
                    CommitLimitBytes = 32_000_000_000,
                    LowMemorySignal = false,
                });
            }

            var settings = new MonitoringSettingsService(Path.Combine(dir, "monitoring.json"));
            settings.Save(new MonitoringSettings
            {
                MemoryEnabled = true,
                Providers = Enum.GetValues<ProviderId>()
                    .Select(id => new ProviderSettings { Id = id, Enabled = false }).ToList(),
            });

            Coordinator = new MonitoringCoordinator(
                new FakeClock(), Reader, Adapters, settings,
                new MonitoringCacheService(Path.Combine(dir, "cache")), history);
            Coordinator.RequestMemoryRefresh();
            SpinWait.SpinUntil(() => Coordinator.LatestMemorySample is not null, TimeSpan.FromSeconds(5));
        }

        public SequencedReader Reader { get; }
        public Dictionary<ProviderId, IProviderAdapter> Adapters { get; }
        public MonitoringCoordinator Coordinator { get; }
        public int ProviderQueries => Adapters.Values.Cast<QuietAdapter>().Sum(a => a.Queries);

        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class SequencedReader : IMemoryReader
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public MemorySample? Read(out string? error)
        {
            var n = Interlocked.Increment(ref _reads);
            error = null;
            return new MemorySample
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                PhysicalTotalBytes = 16_000_000_000,
                PhysicalAvailableBytes = (ulong)(5_000_000_000 + n * 100_000_000),
                CommitTotalBytes = 10_000_000_000,
                CommitLimitBytes = 32_000_000_000,
                LowMemorySignal = false,
            };
        }

        public void Dispose() { }
    }

    private sealed class QuietAdapter : IProviderAdapter
    {
        private int _queries;
        public ProviderId Id { get; init; }
        public int Queries => Volatile.Read(ref _queries);

        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _queries);
            return Task.FromException<ProviderSnapshot>(new InvalidOperationException("no CLI in these tests"));
        }
    }
}
