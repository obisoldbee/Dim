using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OBDim.Models;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.V2;
using OBDim.Monitoring.Network.Windows;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;
using OBDim.UI.Network;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

[Collection("NativeUi")]
public class NetworkReviewTests
{
    private sealed class Source : INetworkObservationSource
    {
        public ObservationSnapshot Current { get; set; } = ObservationSnapshot.Empty;
        public bool IsRunning { get; private set; }
        public event Action? Changed;
        public void SetEnabled(bool value)
        {
            IsRunning = value;
            Current = Current with { Version = Current.Version + 1, State = value ? "active" : "stopped" };
            Changed?.Invoke();
        }
        public void Refresh() { }
    }
    private sealed class Memory : IMemoryReader
    { public MemorySample? Read(out string? error) { error = null; return null; } public void Dispose() { } }
    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "obdim-review-" + Guid.NewGuid().ToString("N"));
        public readonly Source Source = new();
        public readonly MonitoringSettingsService Settings;
        public readonly MonitoringCoordinator Coordinator;
        public Fixture(bool enabled = false)
        {
            Directory.CreateDirectory(DirectoryPath);
            Settings = new(Path.Combine(DirectoryPath, "settings.json"));
            Assert.True(Settings.Save(new MonitoringSettings { NetworkEnabled = enabled, MemoryEnabled = false }));
            Coordinator = new(SystemClock.Instance, new Memory(), new Dictionary<ProviderId, IProviderAdapter>(),
                Settings, new MonitoringCacheService(Path.Combine(DirectoryPath, "cache")), network: Source);
            Coordinator.ApplySettings(Coordinator.Settings);
        }
        public void Dispose() { Coordinator.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "UI/save task did not finish"); task.GetAwaiter().GetResult();
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Keep a real outer WinForms loop alive while async UI continuations run.
            // Standalone DoEvents uninstalls its synchronization context when it exits.
            using var pump = new Form { ShowInTaskbar = false, Opacity = 0 };
            pump.Shown += (_, _) => pump.BeginInvoke(() =>
            {
                try { action(); } catch (Exception ex) { failure = ex; }
                finally { pump.Close(); }
            });
            Application.Run(pump);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA timeout");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OldSettingsDraftCannotUndoLatestPageToggle(bool baseline) => Sta(() =>
    {
        using var fixture = new Fixture(baseline);
        using var settings = new MonitoringSettingsForm(fixture.Coordinator) { ConfirmDiscard = () => true };
        using var panel = new MonitorForm(fixture.Coordinator);
        panel.Show(); panel.SetView(MonitorForm.View.Network); Application.DoEvents();
        var page = All(panel).OfType<NetworkPage>().Single();
        Field<ComboBox>(settings, "_language").SelectedIndex = 1;
        Await(page.SetEnabledAsync(!baseline));
        Await(settings.SaveDraftAsync());
        Assert.Equal(!baseline, fixture.Source.IsRunning);
        Assert.Equal(!baseline, fixture.Coordinator.Settings.NetworkEnabled);
        Assert.Equal(!baseline, fixture.Settings.Load().NetworkEnabled);
        Assert.False(settings.HasUnsavedChanges);
    });
    [Fact]
    public void ExplicitDirtyFieldsWinButUnrelatedLatestFieldsSurvive() => Sta(() =>
    {
        using var fixture = new Fixture();
        using var settings = new MonitoringSettingsForm(fixture.Coordinator) { ConfirmDiscard = () => true };
        Field<CheckBox>(settings, "_network").Checked = true;
        Await(fixture.Coordinator.ApplyAndSaveSettingsAsync(fixture.Coordinator.Settings with { MemoryEnabled = true, NetworkEnabled = true }));
        Await(fixture.Coordinator.ApplyAndSaveSettingsAsync(fixture.Coordinator.Settings with { NetworkEnabled = false }));
        Await(settings.SaveDraftAsync());
        Assert.True(fixture.Source.IsRunning);
        Assert.True(fixture.Settings.Load().NetworkEnabled);
        Assert.True(fixture.Coordinator.Settings.MemoryEnabled);
    });
    [Fact]
    public void StopAppliesBeforeSlowWriteAndQueuedWritesFinishInOrder() => Sta(() =>
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0; var writes = new List<bool>();
        fixture.Settings.BeforeWrite = state =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            writes.Add(state.NetworkEnabled);
        };
        var first = fixture.Coordinator.ApplyAndSaveSettingsAsync(fixture.Coordinator.Settings with { NetworkEnabled = true });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var second = fixture.Coordinator.ApplyAndSaveSettingsAsync(fixture.Coordinator.Settings with { NetworkEnabled = false });
            Assert.False(fixture.Source.IsRunning);
            Thread.Sleep(200); // first disk write is still deliberately blocked
            Assert.False(second.IsCompleted); Assert.Equal(1, Volatile.Read(ref calls));
            release.Set(); Await(Task.WhenAll(first, second));
            Assert.Equal(new[] { true, false }, writes);
            Assert.False(fixture.Settings.Load().NetworkEnabled);
        }
        finally { release.Set(); Await(first); }
    });
    [Fact]
    public void ProductionStopSurvivesFailedSaveAndRenderUntilRetry() => Sta(() =>
    {
        using var fixture = new Fixture(true);
        using var panel = new MonitorForm(fixture.Coordinator);
        panel.Show(); panel.SetView(MonitorForm.View.Network); Application.DoEvents();
        var page = All(panel).OfType<NetworkPage>().Single();
        File.Delete(fixture.Settings.FilePath); Directory.CreateDirectory(fixture.Settings.FilePath);
        Await(page.SetEnabledAsync(false));
        Assert.False(fixture.Source.IsRunning); Assert.False(fixture.Coordinator.Settings.NetworkEnabled);
        for (var i = 0; i < 5; i++) page.Render();
        Assert.True(All(page).Single(c => c.Name == "network-save-status").Visible);
        Assert.Contains("未保存", All(page).Single(c => c.Name == "network-save-status").Text);
        Directory.Delete(fixture.Settings.FilePath);
        Await(page.SetEnabledAsync(false));
        Assert.False(All(page).Single(c => c.Name == "network-save-status").Visible);
        Assert.False(fixture.Settings.Load().NetworkEnabled);
    });
    [Fact]
    public void SharedReviewProbeInputsKeepUnknownAndKnownEndpoints()
    {
        foreach (var test in NetworkV2Tests.Fixture("review-probe-boundaries").GetProperty("cases").EnumerateArray())
        {
            var samples = test.GetProperty("samples").Deserialize<RateSample[]>(NetworkV2Tests.Json)!;
            var at = test.GetProperty("at").GetDateTimeOffset();
            var plot = NetworkSeries.Project(samples, samples[0].InterfaceID, samples[^1].SampledAt.AddSeconds(1), TimeSpan.FromMinutes(1));
            foreach (var upload in new[] { true, false })
            {
                var expected = test.GetProperty(upload ? "upload" : "download");
                double? value = expected.ValueKind == JsonValueKind.Null ? null : expected.GetDouble();
                Assert.Equal(value, NetworkSeries.Probe(plot.Directions[upload], at));
            }
        }
    }
    internal static InterfaceReading LongReading(bool gaps)
    {
        var seed = NetworkV2Tests.Samples("mixed-5s-1s")[0];
        var now = DateTimeOffset.UtcNow; var start = now.AddHours(-2);
        var samples = Enumerable.Range(0, 7201).Select(i => seed with
        {
            SampleID = "long-" + i, SampledAt = start.AddSeconds(i), PublishedAt = start.AddSeconds(i),
            MonotonicNs = (ulong)(i + 1) * 1_000_000_000,
            Cadence = new(1000, 1000, "1s", "source-sampling"),
            Continuity = new(new(i % 30 == 0 && gaps ? "break" : "continuous", "long-" + (i - 1), null), new("continuous", "long-" + (i - 1), null)),
            Rates = new(100 + i % 50, 10000 + i % 200),
        }).ToArray();
        var total = new DirectionTotal(10000, start, 1000000000, true, null);
        return new(seed.InterfaceID, "Review adapter", "Physical", now, samples[^1].Rates, new(total, total), new(10000, 20000), samples, 0, false);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongHistoryHoverReusesActualGeometryAndContextInvalidates(bool gaps) => Sta(() =>
    {
        using var form = new Form { ClientSize = new(540, 700) };
        using var chart = new NetworkTrendControl { Dock = DockStyle.Fill }; form.Controls.Add(chart); form.Show();
        var reading = LongReading(gaps); chart.SetRange(4); chart.Apply(reading, reading.SampledAt!.Value, 1, true);
        Assert.True(chart.CachedPointCount > 100);
        var count = chart.GeometryBuildCount;
        for (var i = 0; i < 1000; i++)
        { chart.SetCursor(reading.Samples[i * 7].SampledAt); if (i % 10 == 0) chart.Refresh(); }
        Assert.Equal(count, chart.GeometryBuildCount);
        chart.Apply(reading, DateTimeOffset.UtcNow, 2, false);
        Assert.Equal(count, chart.GeometryBuildCount); // publication/health only
        chart.Width -= 20; Assert.True(chart.GeometryBuildCount > count); count = chart.GeometryBuildCount;
        chart.SetRange(0); Assert.True(chart.GeometryBuildCount > count); count = chart.GeometryBuildCount;
        chart.Apply(reading with { Samples = reading.Samples.ToArray() }, DateTimeOffset.UtcNow, 3, true);
        Assert.True(chart.GeometryBuildCount > count);
    });
    private sealed class Clock : TimeProvider
    { public long Tick = 1000; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Tick; }
    private sealed class CounterReader : IInterfaceCounterTable
    {
        public Action? Before; public int Calls;
        public IReadOnlyList<InterfaceCounterRow> Rows = [new("nic", "Test NIC", InterfaceKind.Physical, 10, 20)];
        public NetworkReadStatus TryRead(out IReadOnlyList<InterfaceCounterRow> rows, out string? error)
        { Interlocked.Increment(ref Calls); Before?.Invoke(); rows = Rows; error = null; return NetworkReadStatus.Ok; }
    }
    [Fact]
    public void InterfaceLimitIsVisibleBeforeAnyHistoryTruncation() => Sta(() =>
    {
        var reader = new CounterReader { Rows = Enumerable.Range(0, 129).Select(i => new InterfaceCounterRow("nic-" + i, "NIC " + i, InterfaceKind.Physical, 10, 20)).ToArray() };
        using var source = new NetworkObservationService(reader, route: () => "nic-128", schedule: false);
        source.SetEnabled(true); source.Poll();
        Assert.Equal(129, source.Current.SourceInterfaceCount); Assert.Equal(128, source.Current.Interfaces.Count);
        Assert.Equal("nic-128", source.Current.Interfaces[0].Id);
        Assert.All(source.Current.Interfaces, i => Assert.False(i.HistoryTruncated));
        using var form = new Form(); using var page = new NetworkPage(source, _ => true); form.Controls.Add(page); form.Show(); page.Render();
        Assert.Contains(All(page), c => c.Text.Contains("128/129"));
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedReaderOrRouteIsBoundedExplainedAndOldResultFenced(bool routeBlocked)
    {
        var clock = new Clock(); var reader = new CounterReader();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        void Block() { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
        if (!routeBlocked) reader.Before = Block;
        using var source = new NetworkObservationService(reader, clock, () => { if (routeBlocked) Block(); return "nic"; }, false);
        source.SetEnabled(true); var pending = Task.Run(source.Poll);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            source.SetEnabled(false); source.SetEnabled(true);
            Assert.Equal("waiting", source.Current.State);
            clock.Tick += 6000;
            for (var i = 0; i < 30; i++) source.Poll();
            Assert.Equal(1, reader.Calls); Assert.Equal("stalled", source.Current.State);
            source.SetEnabled(false); var version = source.Current.Version;
            release.Set(); await pending;
            Assert.Equal(version, source.Current.Version); Assert.Empty(source.Current.Interfaces);
            source.SetEnabled(true); source.Poll(); Assert.Equal("active", source.Current.State);
            source.Dispose(); version = source.Current.Version; source.Poll(); Assert.Equal(version, source.Current.Version);
        }
        finally { release.Set(); await pending; }
    }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    private static void Key(Control control, Keys key)
    {
        control.Focus();
        Assert.True(PostMessage(control.Handle, 0x100, (IntPtr)key, IntPtr.Zero));
        Assert.True(PostMessage(control.Handle, 0x101, (IntPtr)key, IntPtr.Zero));
        Application.DoEvents();
    }
    [Fact]
    public void NativeMessageDispatchRangesNestedComboAndEscape() => Sta(() =>
    {
        using var fixture = new Fixture(); var reading = LongReading(false);
        fixture.Source.Current = new(1, "test", DateTimeOffset.UtcNow, "active", null, reading.Id, [reading]);
        using var panel = new MonitorForm(fixture.Coordinator); panel.Show(); panel.SetView(MonitorForm.View.Network); Application.DoEvents();
        var page = All(panel).OfType<NetworkPage>().Single();
        Key(page.Chart.RangeButtons[3], Keys.Home); Assert.Equal(0, page.Chart.RangeIndex);
        Key(page.Chart.RangeButtons[0], Keys.Right); Assert.Equal(1, page.Chart.RangeIndex);
        Key(page.Chart.RangeButtons[1], Keys.End); Assert.Equal(4, page.Chart.RangeIndex);
        Key(page.Chart, Keys.End); Assert.True(page.Chart.HasReading);
        var combo = All(page).OfType<ComboBox>().Single();
        foreach (var key in new[] { Keys.D1, Keys.D2, Keys.D3, Keys.D4 })
        { Key(combo, key); Assert.Equal(MonitorForm.View.Network, panel.CurrentView); }
        var stop = All(page).Single(c => c.Name == "network-toggle");
        Key(stop, Keys.Escape); Assert.False(page.Chart.HasReading); Assert.True(panel.Visible);
        Key(stop, Keys.Escape); Assert.False(panel.Visible);
    });
}
