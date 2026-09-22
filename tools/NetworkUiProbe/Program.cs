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
        using var coordinator = new MonitoringCoordinator(SystemClock.Instance, new WindowsMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter>(), settings, new MonitoringCacheService(Path.Combine(output, "cache")));
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
                form.SetView(MonitorForm.View.Network);
                await Task.Delay(6500);
                form.Show(); form.Activate(); form.SetView(MonitorForm.View.Network);
                var page = All(form).OfType<NetworkPage>().Single();
                page.Chart.SetRange(0); page.Render(); Capture("network-native-1m");
                page.Chart.SetRange(3); Capture("network-native-1h");
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
                for (var i = 0; i < 100; i++)
                {
                    form.SetView(MonitorForm.View.Memory);
                    await Task.Delay(1);
                    var watch = Stopwatch.StartNew();
                    var tab = (Button)typeof(MonitorForm).GetField("_networkSegment", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    tab.PerformClick(); page.Refresh(); page.Chart.Refresh();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                    await Task.Delay(1);
                }
                var sorted = samples.Order().ToArray();
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
                    ForegroundProcess = foregroundName, Method = "SystemAware WinForms message loop; native PerformClick -> target subtree synchronous WM_PAINT; 100 hot memory-to-network transitions. No HTML.",
                    Samples = samples, MedianMs = sorted[49], P95Ms = sorted[94], MaxMs = sorted[^1],
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
}
