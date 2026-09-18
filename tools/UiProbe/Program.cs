using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string _output = "";
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }
    private sealed class Memory : IMemoryReader
    {
        public MemorySample? Read(out string? error)
        { error = null; return new() { SampledAtUtc = DateTimeOffset.UtcNow, PhysicalTotalBytes = 32UL*1024*1024*1024,
            PhysicalAvailableBytes = 12UL*1024*1024*1024, CommitTotalBytes = 24UL*1024*1024*1024,
            CommitLimitBytes = 48UL*1024*1024*1024, LowMemorySignal = false }; }
        public void Dispose() { }
    }
    private sealed class Adapter(ProviderId id, Clock clock) : IProviderAdapter
    {
        public ProviderId Id => id;
        public double Remaining = 65;
        public int StartupDelayMilliseconds;
        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken token)
        {
            // Deliberately block only this diagnostic adapter, to simulate synchronous CLI discovery/startup.
            if (StartupDelayMilliseconds > 0) Thread.Sleep(StartupDelayMilliseconds);
            return Task.FromResult(new ProviderSnapshot
        {
            Provider = Id, IdentityKey = "fixture", IdentityVerified = true,
            AttemptedAtUtc = clock.UtcNow, SucceededAtUtc = clock.UtcNow,
            Buckets = [new QuotaBucket { SourceKey = Id == ProviderId.Ark ? "coding-plan" : "codex",
                Tier = "pro", Windows = [new QuotaWindow { SourceKey = "primary", WindowDurationMinutes = 300,
                    RemainingPercent = Remaining, UsedPercent = 100-Remaining, HasAnyQuotaField = true,
                    DisplayAsUsed = Id != ProviderId.Codex, ResetsAtUtc = DateTimeOffset.UtcNow.AddHours(5) }] }],
        });
        }
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    private static int Gui(int flag) => GetGuiResources(Process.GetCurrentProcess().Handle, flag);
    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static MonitorForm Create(MonitoringCoordinator coordinator)
    {
        var ctor = typeof(MonitorForm).GetConstructors().Single();
        var args = new object?[ctor.GetParameters().Length]; args[0] = coordinator;
        return (MonitorForm)ctor.Invoke(args);
    }
    private static void Capture(Form form, string name)
    {
        form.Refresh();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(_output, name + ".png"));
    }
    private static async Task Settle(MonitoringCoordinator coordinator)
    {
        var watch = Stopwatch.StartNew();
        while (coordinator.GetDisplayStates().Any(s => s.Refreshing))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("fixture refresh");
            await Task.Delay(5);
        }
        await Task.Delay(15);
    }

    /// <summary>
    /// Memory sampling is single-flight and off the UI thread now, so "ask, then wait for it to
    /// land" is the only correct way to drive it from a probe.
    /// </summary>
    private static async Task SampleOnce(MonitoringCoordinator coordinator)
    {
        coordinator.RequestMemoryRefresh();
        var watch = Stopwatch.StartNew();
        while (coordinator.MemoryRefreshInFlight && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(2);
        await Task.Delay(5);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length>1 && args[1]=="--auth-parity") { AuthParityProbe.Run(args[0]); return; }
        _output = Path.GetFullPath(args[0]); Directory.CreateDirectory(_output);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var fixture = Path.Combine(_output, "fixture-state"); Directory.CreateDirectory(fixture);
        var settings = new MonitoringSettingsService(Path.Combine(fixture, "monitoring.json"));
        settings.Save(new MonitoringSettings { Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings { Id = id, Enabled = true }).ToList() });
        var clock = new Clock();
        var adapters = Enum.GetValues<ProviderId>().ToDictionary(id => id, id => new Adapter(id, clock));
        var constructor = typeof(MonitoringCoordinator).GetConstructors().Single();
        using var coordinator = (MonitoringCoordinator)constructor.Invoke([clock, new Memory(), adapters.ToDictionary(p => p.Key, p => (IProviderAdapter)p.Value), settings, new MonitoringCacheService(Path.Combine(fixture, "cache")), null]);
        var form = Create(coordinator);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Shown += async (_, _) =>
        {
            try
            {
                coordinator.RequestManualRefreshAll();
                await SampleOnce(coordinator);
                await Settle(coordinator);
                form.Refresh();
                var dpi = form.DeviceDpi;
                var originalRows = Descendants(form).Where(c => c.Tag is "t").ToArray();
                var destroyed = 0;
                foreach (var row in originalRows) row.Disposed += (_, _) => destroyed++;
                clock.UtcNow = clock.UtcNow.AddSeconds(16); adapters[ProviderId.Codex].Remaining = 64;
                coordinator.RequestManualRefresh(ProviderId.Codex); await Settle(coordinator); form.Refresh();
                Capture(form, "quota");
                var disposedOnValueChange = destroyed;

                var samples = new List<double>();
                var heartbeatDelays = new List<double>();
                var heartbeatWatch = Stopwatch.StartNew();
                using var heartbeat = new System.Windows.Forms.Timer { Interval = 16 };
                heartbeat.Tick += (_, _) => { heartbeatDelays.Add(heartbeatWatch.Elapsed.TotalMilliseconds); heartbeatWatch.Restart(); };
                heartbeat.Start();
                clock.UtcNow = clock.UtcNow.AddSeconds(16);
                foreach (var adapter in adapters.Values) adapter.StartupDelayMilliseconds = 150;
                var requestWatch = Stopwatch.StartNew();
                coordinator.RequestManualRefreshAll();
                var slowStartupRequestMilliseconds = requestWatch.Elapsed.TotalMilliseconds;
                await Settle(coordinator);
                foreach (var adapter in adapters.Values) adapter.StartupDelayMilliseconds = 0;
                for (var i = 0; i < 200; i++)
                {
                    var field = i % 2 == 0 ? "_memorySegment" : "_quotaSegment";
                    var button = (Button)typeof(MonitorForm).GetField(field, Private)!.GetValue(form)!;
                    var watch = Stopwatch.StartNew();
                    button.PerformClick();
                    var panel = (Control)typeof(MonitorForm).GetField(i % 2 == 0 ? "_memoryView" : "_quotaView", Private)!.GetValue(form)!;
                    panel.Refresh(); // measure through native subtree paint, not merely SetView return
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                    await Task.Delay(1);
                }
                heartbeat.Stop();
                form.SetView(MonitorForm.View.Memory); Capture(form, "memory");

                // ---- memory page: hot metric/range switching with real paints and real handles ----
                var chart = Descendants(form).Single(c => c.GetType().Name == "MemoryTrendPanel");
                var chartType = chart.GetType();
                long Builds() => Convert.ToInt64(chartType.GetProperty("GeometryBuildCount",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart));

                // Seed a full two hours so the widest range is drawn from real coverage, not a stub.
                var seed = DateTimeOffset.UtcNow.AddMinutes(-121);
                for (var i = 0; i <= 1440; i++)
                {
                    coordinator.MemoryHistory.Add(new MemorySample
                    {
                        SampledAtUtc = seed.AddSeconds(i * 5.0),
                        PhysicalTotalBytes = 32UL * 1024 * 1024 * 1024,
                        PhysicalAvailableBytes = (ulong)(12L * 1024 * 1024 * 1024 + Math.Sin(i / 37.0) * 2.2e9),
                        CommitTotalBytes = (ulong)(24L * 1024 * 1024 * 1024 + Math.Cos(i / 41.0) * 1.4e10),
                        CommitLimitBytes = 48UL * 1024 * 1024 * 1024,
                        LowMemorySignal = false,
                    });
                }

                var segmentButtons = Descendants(form).OfType<Button>().Where(b =>
                    !string.IsNullOrEmpty(b.AccessibleName)
                    && b.Text != "⟳" && b.Text != "⚙"
                    && b.Text != LocalizationService.Get("monitor.tab_quota")
                    && b.Text != LocalizationService.Get("monitor.tab_memory")
                    && b.Text != LocalizationService.Get("monitor.open_task_manager")).ToList();

                // The page follows the coordinator's state event; seeding the buffer directly does
                // not raise one, so push a real sample through before measuring the chart.
                await SampleOnce(coordinator);
                form.Refresh();
                Application.DoEvents();

                var buildsBefore = Builds();
                var gdiMemoryBefore = Gui(0); var userMemoryBefore = Gui(1);
                var controlsMemoryBefore = Descendants(form).Count();
                var memorySwitches = new List<double>();
                for (var i = 0; i < 100; i++)
                {
                    var switchWatch = Stopwatch.StartNew();
                    segmentButtons[i % segmentButtons.Count].PerformClick();
                    chart.Refresh();
                    memorySwitches.Add(switchWatch.Elapsed.TotalMilliseconds);
                    await Task.Delay(1);
                }
                var buildsAfter = Builds();
                var gdiMemoryAfter = Gui(0); var userMemoryAfter = Gui(1);
                var controlsMemoryAfter = Descendants(form).Count();
                memorySwitches.Sort();

                void Select(string metric, string range)
                {
                    var metricProperty = chartType.GetProperty("Metric")!;
                    var rangeProperty = chartType.GetProperty("Range")!;
                    metricProperty.SetValue(chart, Enum.Parse(metricProperty.PropertyType, metric));
                    rangeProperty.SetValue(chart, Enum.Parse(rangeProperty.PropertyType, range));
                    form.Refresh();
                    Application.DoEvents();
                }

                Select("PhysicalUsed", "Hour1"); Capture(form, "memory-physical-1h");
                Select("Commit", "Hour1"); Capture(form, "memory-commit-1h");
                Select("PhysicalUsed", "Hour2"); Capture(form, "memory-physical-2h");
                Select("Commit", "Hour2"); Capture(form, "memory-commit-2h");
                var beforeHidden = Descendants(form).Count();
                var hiddenQuotaLayouts = 0;
                var rowPanels = (Dictionary<ProviderId, FlowLayoutPanel>)typeof(MonitorForm).GetField("_cardRows", Private)!.GetValue(form)!;
                foreach (var panel in rowPanels.Values) panel.Layout += (_, _) => hiddenQuotaLayouts++;
                for (var i = 0; i < 12; i++)
                {
                    await SampleOnce(coordinator);
                }
                await Task.Delay(30);
                var afterHidden = Descendants(form).Count();
                var layoutsFor12Samples = hiddenQuotaLayouts;
                var reopenSamples = new List<double>();
                var starts = 1; var gdiBefore = Gui(0); var userBefore = Gui(1);
                for (var i = 0; i < 100; i++)
                {
                    var reopening = Stopwatch.StartNew();
                    typeof(MonitorForm).GetMethod("OnDeactivate", Private)!.Invoke(form, [EventArgs.Empty]);
                    if (form.IsDisposed) { form = Create(coordinator); starts++; }
                    form.Show(); form.Refresh();
                    reopenSamples.Add(reopening.Elapsed.TotalMilliseconds);
                }
                var gdiAfter = Gui(0); var userAfter = Gui(1);
                var windowType = typeof(MonitorForm).Assembly.GetType("OBDim.UI.MonitoringSettingsForm");
                if (windowType is not null)
                {
                    form.Hide();
                    using var editor = (Form)Activator.CreateInstance(windowType, [coordinator, null, null])!;
                    editor.Show();
                    for (var i = 0; i < 5; i++)
                    {
                        windowType.GetMethod("SelectCategory", Private)!.Invoke(editor, [i]);
                        editor.Refresh(); Capture(editor, "settings-" + i);
                    }
                }
                else
                {
                    form.SetView((MonitorForm.View)Enum.Parse(typeof(MonitorForm.View), "Settings"));
                    Capture(form, "settings-0");
                }
                samples.Sort(); reopenSamples.Sort();
                File.WriteAllText(Path.Combine(_output, "metrics.json"), JsonSerializer.Serialize(new
                {
                    Method = "Release SystemAware native controls; programmatic tab click plus synchronous target subtree paint; fixture CLI and cache only",
                    OS = Environment.OSVersion.ToString(), Dpi = dpi, Samples = samples.Count,
                    P50Milliseconds = samples[samples.Count/2], P95Milliseconds = samples[(int)Math.Ceiling(samples.Count*.95)-1],
                    MaxMilliseconds = samples[^1], MaxHeartbeatIntervalMilliseconds = heartbeatDelays.DefaultIfEmpty().Max(),
                    MetricTitlesDisposedOnValueChange = disposedOnValueChange, ControlsBeforeHiddenMemory = beforeHidden, ControlsAfterHiddenMemory = afterHidden,
                    HiddenQuotaLayoutsFor12MemorySamples = layoutsFor12Samples,
                    SlowStartupRequestMilliseconds = slowStartupRequestMilliseconds,
                    ReopenP95Milliseconds = reopenSamples[94],
                    PopoverInstancesFor100Reopens = starts, GdiBefore = gdiBefore, GdiAfter = gdiAfter, UserBefore = userBefore, UserAfter = userAfter,
                    MemorySegmentButtons = segmentButtons.Count,
                    MemorySwitchP50Milliseconds = memorySwitches[49],
                    MemorySwitchP95Milliseconds = memorySwitches[94],
                    MemorySwitchMaxMilliseconds = memorySwitches[^1],
                    MemoryGeometryBuildsFor100Switches = buildsAfter - buildsBefore,
                    MemoryControlsBefore = controlsMemoryBefore, MemoryControlsAfter = controlsMemoryAfter,
                    MemoryGdiBefore = gdiMemoryBefore, MemoryGdiAfter = gdiMemoryAfter,
                    MemoryUserBefore = userMemoryBefore, MemoryUserAfter = userMemoryAfter,
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(File.ReadAllText(Path.Combine(_output, "metrics.json")));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_output, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            finally { form.Dispose(); Application.ExitThread(); }
        };
        form.Show(); Application.Run();
    }
}
