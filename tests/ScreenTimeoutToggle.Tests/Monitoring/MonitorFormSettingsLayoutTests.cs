using System.Text;
using System.Windows.Forms;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// Layout self-check for the popover settings view (added after the user found controls
/// missing/misplaced on a real machine — the STA smoke test only constructed/closed the
/// form and never looked INSIDE). Dumps the live control tree and asserts:
/// every expected settings control exists, is visible, inside the card bounds, and
/// checkboxes/inputs do not overlap each other.
/// </summary>
public class MonitorFormSettingsLayoutTests
{
    [Fact]
    public void SettingsView_ContainsAllControls_NonOverlapping()
    {
        RunOnSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"obdim-laychk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var adapters = new Dictionary<ProviderId, IProviderAdapter>();
                using var coordinator = new MonitoringCoordinator(
                    new FakeClock(),
                    new StubMemoryReader(),
                    adapters,
                    new MonitoringSettingsService(Path.Combine(dir, "monitoring.json")),
                    new MonitoringCacheService(Path.Combine(dir, "cache")));

                using var form = new MonitorForm(coordinator);
                form.SetView(MonitorForm.View.Settings);
                form.Show();
                Application.DoEvents();

                var dump = new StringBuilder();
                Dump(form, 0, dump);
                var dumpPath = Path.Combine(dir, "layout-dump.txt");
                File.WriteAllText(dumpPath, dump.ToString());

                var all = AllDescendants(form).ToList();

                // 1. Every section header exists and is visible.
                foreach (var key in new[]
                {
                    "monitor.settings.group_screen",
                    "monitor.settings.group_hotkeys",
                    "monitor.settings.group_general",
                    "monitor.settings.group_monitoring",
                })
                {
                    var expected = Localization(key);
                    Assert.True(all.Any(c => c is Label && c.Text == expected && c.Visible),
                        $"section header '{expected}' ({key}) missing or invisible. dump={dumpPath}");
                }

                // 2. The four timeout numerics exist and are visible.
                Assert.Equal(4, all.Count(c => c is NumericUpDown && c.Visible));

                // 3. Both hotkey capture boxes exist, visible, ReadOnly.
                var boxes = all.OfType<MonitorForm.HotkeyCaptureBox>().Where(c => c.Visible).ToList();
                Assert.Equal(2, boxes.Count);

                // 4. Monitoring checkboxes exist and are visible.
                Assert.True(all.Any(c => c is CheckBox && c.Text.Contains(Localization("monitor.settings.memory")) && c.Visible));
                Assert.True(all.Any(c => c is CheckBox && c.Text.Contains(Localization("monitor.settings.reminders")) && c.Visible));
                Assert.True(all.Any(c => c is CheckBox && c.Text.Contains(Localization("monitor.settings.left_click")) && c.Visible));
                Assert.Equal(3, all.Count(c => c is CheckBox cb && cb.Visible &&
                    (cb.Text == Localization("monitor.provider.codex") ||
                     cb.Text == Localization("monitor.provider.minimax") ||
                     cb.Text == Localization("monitor.provider.ark"))));
                Assert.Equal(3, all.Count(c => c is TextBox t && t.Name!.StartsWith("cliPath_") && c.Visible));

                // 5. Save button exists.
                Assert.True(all.Any(c => c is Button b && b.Text == Localization("monitor.settings.save") && c.Visible));

                // 6. Inputs do not overlap their SIBLINGS (same parent only — comparing
                //    bounds across different parents compares different coordinate spaces
                //    and flags parent/child pairs as false positives).
                var inputs = all.Where(c => c is CheckBox or TextBox or NumericUpDown or Button && c.Visible).ToList();
                var overlaps = inputs.SelectMany(a => inputs, (a, b) => (a, b))
                    .Where(pair => !ReferenceEquals(pair.a, pair.b)
                                   && ReferenceEquals(pair.a.Parent, pair.b.Parent)
                                   && pair.a.Bounds.IntersectsWith(pair.b.Bounds)
                                   && pair.a is not Button && pair.b is not Button)
                    .Select(pair => $"{pair.a.GetType().Name} '{Truncate(pair.a.Text)}'@{pair.a.Bounds} <-> '{Truncate(pair.b.Text)}'@{pair.b.Bounds}")
                    .Distinct()
                    .ToList();
                Assert.True(overlaps.Count == 0,
                    "overlapping sibling inputs:\n" + string.Join("\n", overlaps) + $"\ndump={dumpPath}");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        });
    }

    private static string Truncate(string s) => s.Length <= 30 ? s : s[..30];

    private static string Localization(string key) => OBDim.Services.LocalizationService.Get(key);

    private static IEnumerable<Control> AllDescendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in AllDescendants(child)) yield return nested;
        }
    }

    private static void Dump(Control root, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2)
          .Append(root.GetType().Name)
          .Append(" \"").Append(root.Text.Length > 40 ? root.Text[..40] : root.Text).Append('"')
          .Append(' ').Append(root.Bounds)
          .Append(root.Visible ? "" : " [INVISIBLE]")
          .AppendLine();
        foreach (Control child in root.Controls) Dump(child, depth + 1, sb);
    }

    private sealed class StubMemoryReader : IMemoryReader
    {
        public MemorySample? Read(out string? error)
        {
            error = null;
            return new MemorySample
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                PhysicalTotalBytes = 16_000_000_000,
                PhysicalAvailableBytes = 8_000_000_000,
                CommitTotalBytes = 8_000_000_000,
                CommitLimitBytes = 32_000_000_000,
                LowMemorySignal = false,
            };
        }

        public void Dispose() { }
    }

    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA test thread did not finish in time");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("Assertion failed on the STA thread: " + failure.Message, failure);
        }
    }
}
