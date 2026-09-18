using System.Windows.Forms;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// Regression guard for the single-character tab bug: LayoutTabs sized the 额度/内存 tab
/// buttons at MeasureText width + 6px. A themed (visual-styles) Button in a DPI-AWARE
/// process insets its text rectangle ~8px, and GDI WordBreak wraps CJK text between any
/// two characters — so with only +6 the second character silently moved onto a second
/// line that the 30px-tall button clips, and production rendered just 额/内.
///
/// The guard asserts the sizing invariant (button width ≥ measured text width + 8px
/// scaled by the form DPI) instead of rendered pixels on purpose: the xunit host is
/// DPI-UNAWARE, where the inset is only ~6px, so a pixel-rendered assertion can never
/// go red for this exact bug class in-process (verified: DPI-unaware hosts fit 额度 in
/// 54px; SystemAware + visual styles need 56px — see docs/research/260913/tab-repro).
/// </summary>
[Collection("NativeUi")]
public class MonitorFormTabLabelTests
{
    [Fact]
    public void TabButtons_AreWideEnoughForThemedDpiAwareTextInset()
    {
        RunOnSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"obdim-tabchk-{Guid.NewGuid():N}");
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
                form.SetView(MonitorForm.View.Quota);
                form.Show();
                Application.DoEvents();

                // Empirical threshold (docs/research/260913/tab-repro, width scan 44..72):
                // 额度 fits once the button is MeasureText + 8px at 96 DPI.
                var requiredInset = (int)Math.Round(8 * (form.DeviceDpi / 96.0));
                foreach (var key in new[] { "monitor.tab_quota", "monitor.tab_memory" })
                {
                    var text = OBDim.Services.LocalizationService.Get(key);
                    var tab = Descendants(form).OfType<Button>().FirstOrDefault(b => b.Text == text && b.Visible);
                    Assert.True(tab is not null, $"tab button '{text}' ({key}) not found");

                    var measured = TextRenderer.MeasureText(tab.Text, tab.Font).Width;
                    Assert.True(tab.Width >= measured + requiredInset,
                        $"tab '{text}' is {tab.Width}px wide but needs measured text {measured}px + " +
                        $"{requiredInset}px themed/DPI-aware inset — narrower buttons wrap the second " +
                        "CJK character onto a clipped line (the 额/内 single-character tab bug).");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
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
