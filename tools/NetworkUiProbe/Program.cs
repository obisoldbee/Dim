using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Network.V2;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI;
using OBDim.UI.Network;

internal static class Program
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        LocalizationService.CurrentLanguage = args.Contains("--english") ? "en-US" : "zh-CN";
        var settings = new MonitoringSettingsService(Path.Combine(output, "probe-monitoring.json"));
        settings.Save(new MonitoringSettings { NetworkEnabled = true, Providers = MonitoringSettings.CreateDefaultProviders() });
        var scenario = args.FirstOrDefault(a => a.StartsWith("--scenario="))?.Split('=')[1] ?? "live";
        using var stress = scenario == "live" ? null : new StressSource(scenario);
        using var coordinator = new MonitoringCoordinator(SystemClock.Instance, new WindowsMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter>(), settings, new MonitoringCacheService(Path.Combine(output, "cache")), network: stress);
        using var form = new MonitorForm(coordinator);
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(80, 40);
        coordinator.Start();
        var error = 0;
        void Capture(string name)
        {
            form.Refresh();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        form.Shown += async (_, _) =>
        {
            try
            {
                if (args.Contains("--tab-navigation"))
                {
                    CheckTabNavigation(form, output, Capture);
                    form.Close();
                    return;
                }
                form.SetView(MonitorForm.View.Network);
                await Task.Delay(scenario == "live" ? 6500 : 500);
                form.Show(); form.Activate(); form.SetView(MonitorForm.View.Network);
                var page = All(form).OfType<NetworkPage>().Single();
                page.Chart.SetRange(0); page.Render(); Capture("network-native-1m");
                page.Chart.SetRange(scenario == "live" ? 3 : 4); Capture("network-native-history");
                var process = Process.GetCurrentProcess();
                object Resources()
                {
                    process.Refresh();
                    return new { Controls = All(form).Count(), Handles = process.HandleCount,
                        Gdi = GetGuiResources(process.Handle, 0), User = GetGuiResources(process.Handle, 1),
                        WorkingSetBytes = process.WorkingSet64, CpuMs = process.TotalProcessorTime.TotalMilliseconds };
                }
                for (var warm = 0; warm < 20; warm++)
                {
                    form.SetView(MonitorForm.View.Memory); form.Refresh();
                    form.SetView(MonitorForm.View.Network); form.Refresh();
                    await Task.Delay(1);
                }
                var before = Resources();
                var samples = new List<double>();
                var frames = new List<object>();
                for (var i = 0; i < 100; i++)
                {
                    form.SetView(MonitorForm.View.Memory);
                    await Task.Delay(1);
                    var queued = Stopwatch.GetTimestamp();
                    var dispatched = new TaskCompletionSource<double>();
                    form.BeginInvoke(() => dispatched.SetResult(Stopwatch.GetElapsedTime(queued).TotalMilliseconds));
                    var queueMs = await dispatched.Task;
                    var gcBefore = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                    if (!form.Visible) throw new InvalidOperationException("Probe lost visibility; measurements rejected. Run without competing UI tests.");
                    var paintsBefore = page.Chart.PaintCount;
                    var watch = Stopwatch.StartNew();
                    var tab = (Button)typeof(MonitorForm).GetField("_networkSegment", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    tab.PerformClick(); page.Refresh(); page.Chart.Refresh();
                    if (!page.Visible || page.Chart.PaintCount <= paintsBefore)
                        throw new InvalidOperationException("Target chart did not paint; transition rejected.");
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    samples.Add(elapsed);
                    var snapshot = coordinator.Network.Current;
                    var native = stress?.Service ?? coordinator.Network as NetworkObservationService;
                    frames.Add(new { InputToPaintMs = elapsed, QueueMs = queueMs, PaintDelta = page.Chart.PaintCount - paintsBefore, Visible = form.Visible && page.Visible,
                        page.Chart.LastProjectionMs, page.Chart.LastGeometryMs, page.Chart.LastPaintMs, page.Chart.GeometryBuildCount,
                        snapshot.Version, Interfaces = snapshot.Interfaces.Count, Samples = snapshot.Interfaces.Sum(i => i.Samples.Count),
                        SelectedSamples = snapshot.Interfaces.FirstOrDefault(i => i.Id == snapshot.SystemInterfaceID)?.Samples.Count ?? 0,
                        GapMarkers = snapshot.Interfaces.FirstOrDefault(i => i.Id == snapshot.SystemInterfaceID)?.Samples.Count(s => s.Continuity.Upload.Reason is not (null or "start") || s.Continuity.Download.Reason is not (null or "start")) ?? 0,
                        page.Chart.RangeIndex, Viewport = new { page.Chart.Width, page.Chart.Height, page.Chart.DeviceDpi },
                        CurrentLockWaitMs = native?.LastCurrentLockWaitMs, SourceReadMs = native?.LastReadMs, SourcePublishMs = native?.LastPublishMs,
                        GcDelta = new[] { GC.CollectionCount(0)-gcBefore[0], GC.CollectionCount(1)-gcBefore[1], GC.CollectionCount(2)-gcBefore[2] } });
                    await Task.Delay(1);
                }
                var sorted = samples.Order().ToArray();
                // Suspend fixture updates only for the isolated cursor/cache measurement.
                stress?.PauseUpdates();
                var builds = page.Chart.GeometryBuildCount;
                var hoverAlloc = GC.GetAllocatedBytesForCurrentThread();
                var hoverWatch = Stopwatch.StartNew();
                var bounds = page.Chart.PlotBounds(true);
                for (var move = 0; move < 1000; move++)
                {
                    var x = bounds.Left + move % bounds.Width; var y = bounds.Top + bounds.Height / 2;
                    SendMessage(page.Chart.Handle, 0x200, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
                    if (move % 50 == 0) page.Chart.Refresh();
                }
                var hover = new { Moves = 1000, ElapsedMs = hoverWatch.Elapsed.TotalMilliseconds,
                    AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - hoverAlloc,
                    GeometryBuilds = page.Chart.GeometryBuildCount - builds, CachedPoints = page.Chart.CachedPointCount,
                    Method = "Own chart HWND WM_MOUSEMOVE; 20 synchronous paints; allocations include input/labels/GDI wrappers, not just hit testing" };
                var after = Resources();
                var buttons = page.Chart.RangeButtons.Select(b => new {
                    b.Text, b.Width, b.Height, b.Left, b.Right,
                    MeasuredTextWidth = TextRenderer.MeasureText(b.Text, b.Font).Width,
                    Fits = b.Width >= TextRenderer.MeasureText(b.Text, b.Font).Width + 8 * form.DeviceDpi / 96
                }).ToArray();
                var foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out var foregroundPid);
                var foregroundName = Process.GetProcessById((int)foregroundPid).ProcessName;
                var dpiChecks = new List<object>();
                // Native GDI text metrics at each target logical scale; explicitly not an
                // OS display-DPI switch or a screenshot at a different display setting.
                foreach (var dpi in new[] { 96, 120, 144, 192 })
                {
                    using var font = new Font("Microsoft YaHei UI", 9F * dpi / 96);
                    var width = (int)Math.Round((page.Chart.Width * 96.0 / page.Chart.DeviceDpi - 24) * dpi / 96);
                    foreach (var label in new[] { "1 分钟", "10 分钟", "30 分钟", "1 小时", "2 小时", "1 min", "10 min", "30 min", "1 hour", "2 hours" })
                    {
                        var measured = TextRenderer.MeasureText(label, font).Width;
                        dpiChecks.Add(new { Dpi = dpi, Label = label, ButtonWidth = width / 5, TextWidth = measured,
                            Fits = width / 5 >= measured + 8 * dpi / 96 });
                    }
                }
                form.ClientSize = new Size(540 * form.DeviceDpi / 96, 600 * form.DeviceDpi / 96);
                Capture("network-native-small-height");
                var report = new
                {
                    Assembly = typeof(MonitorForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    OS = Environment.OSVersion.ToString(), Dpi = form.DeviceDpi, Screens = Screen.AllScreens.Select(s => new { s.Bounds, s.WorkingArea, s.Primary }),
                    ForegroundProcess = foregroundName, ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount, Method = "SystemAware WinForms message loop; native PerformClick -> target subtree synchronous WM_PAINT; 100 hot memory-to-network transitions. No HTML.",
                    Scenario = scenario, FixtureUpdatesDuringTransitions = stress is not null, Frames = frames, Hover = hover,
                    Samples = samples, MedianMs = (sorted[49] + sorted[50]) / 2, P95Ms = sorted[94], P99Ms = sorted[98], MaxMs = sorted[^1], Over100Ms = sorted.Count(s => s > 100),
                    Before = before, After = after, Buttons = buttons, DpiTextLayoutChecks = dpiChecks,
                    DpiLimit = "Only current desktop DPI is a live display measurement. 120/144/192 entries are native GDI text/width stress checks; OS DPI switching and cross-monitor behavior not verified.",
                    Snapshot = new { coordinator.Network.Current.State, coordinator.Network.Current.Capabilities, Interfaces = coordinator.Network.Current.Interfaces.Count },
                };
                File.WriteAllText(Path.Combine(output, "native-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                if (buttons.Any(b => !b.Fits) || sorted[94] >= 100) throw new InvalidOperationException("Native layout/performance gate failed");
                if (args.Contains("--stay"))
                {
                    form.ClientSize = new Size(540 * form.DeviceDpi / 96, 760 * form.DeviceDpi / 96);
                    form.Show(); form.Activate();
                    return;
                }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); error = 1; }
            form.Close();
        };
        Application.Run(form);
        Environment.ExitCode = error;
    }

    private static void CheckTabNavigation(MonitorForm form, string output, Action<string> capture)
    {
        var foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out var pid);
        var foregroundName = Process.GetProcessById((int)pid).ProcessName;
        if (foregroundName == "LockApp") throw new InvalidOperationException("Unlock the desktop before keyboard validation.");
        Button Tab(string field) => (Button)typeof(MonitorForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var quota = Tab("_quotaSegment"); var memory = Tab("_memorySegment"); var network = Tab("_networkSegment");
        var tabs = new[] { quota, memory, network };
        var page = All(form).OfType<NetworkPage>().Single();
        var initialRange = page.Chart.RangeIndex;
        var clicks = 0;
        foreach (var button in tabs.Concat(new[] { Tab("_refreshButton"), Tab("_settingsButton") })) button.Click += (_, _) => clicks++;
        var frames = new List<object>(); var timings = new List<double>();
        if (form.CurrentView != MonitorForm.View.Quota || !quota.Focused)
            throw new InvalidOperationException("Opening did not select and focus the quota page.");
        capture("tab-native-start-quota");
        for (var i = 0; i < 300; i++)
        {
            // Never call SetView or Focus between keys: that masked the missing
            // network -> quota transition in the previous acceptance harness.
            var focused = tabs.Single(t => t.Focused);
            var expected = (MonitorForm.View)((i + 1) % 3);
            var watch = Stopwatch.StartNew();
            if (!PostMessage(focused.Handle, 0x100, (IntPtr)Keys.Tab, IntPtr.Zero) ||
                !PostMessage(focused.Handle, 0x101, (IntPtr)Keys.Tab, IntPtr.Zero))
                throw new InvalidOperationException("Could not post Tab to the probe's own control.");
            Application.DoEvents();
            form.Refresh();
            if (!form.Visible || !tabs[(int)expected].Focused || form.CurrentView != expected || page.Chart.RangeIndex != initialRange)
                throw new InvalidOperationException($"Tab {i + 1} did not select {expected}: visible={form.Visible}, page={form.CurrentView}.");
            var elapsed = watch.Elapsed.TotalMilliseconds;
            timings.Add(elapsed); frames.Add(new { KeyNumber = i + 1, Page = form.CurrentView.ToString(), InputToPaintMs = elapsed, FocusedTab = tabs[(int)expected].Text });
            if (i < 3) capture("tab-native-" + form.CurrentView.ToString().ToLowerInvariant());
        }
        var buttons = new[] { quota, memory, network }.Select(b => new { b.Text, b.Width,
            TextWidth = TextRenderer.MeasureText(b.Text, b.Font).Width,
            Fits = b.Width >= TextRenderer.MeasureText(b.Text, b.Font).Width + 8 * form.DeviceDpi / 96 }).ToArray();
        if (clicks != 0 || buttons.Any(b => !b.Fits)) throw new InvalidOperationException("Tab unexpectedly clicked a button or clipped a label.");
        var sorted = timings.Order().ToArray();
        var report = new
        {
            Assembly = typeof(MonitorForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Method = "SystemAware native WinForms; 300 consecutive queued Tab keys, DoEvents dispatch and synchronous Refresh. 100 complete quota-memory-network-quota cycles. No Focus/SetView reset, Enter or Click between keys.",
            Dpi = form.DeviceDpi, ForegroundProcess = foregroundName, Frames = frames, Clicks = clicks, Buttons = buttons,
            MedianMs = (sorted[149] + sorted[150]) / 2, P95Ms = sorted[284], P99Ms = sorted[296], MaxMs = sorted[^1],
            CompletedCycles = 100, RangeUnchanged = page.Chart.RangeIndex == initialRange,
            DpiLimit = "Only current desktop DPI measured; no OS scaling switch or cross-monitor validation.",
            ScreenshotMethod = "DrawToBitmap of live production MonitorForm controls in the native probe, not the installed tray process or HTML."
        };
        File.WriteAllText(Path.Combine(output, "tab-native-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
