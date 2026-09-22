using System.Reflection;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Network.V2;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI;
using OBDim.UI.Network;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

[Collection("NativeUi")]
public class NetworkPageTests
{
    private sealed class Source : INetworkObservationSource
    {
        public ObservationSnapshot Current { get; set; } = ObservationSnapshot.Empty;
        public bool IsRunning { get; private set; }
        public int Starts, Refreshes;
        public event Action? Changed;
        public void SetEnabled(bool enabled) { if (enabled) Starts++; IsRunning = enabled; Changed?.Invoke(); }
        public void Refresh() => Refreshes++;
    }
    private sealed class Reader : IMemoryReader
    { public MemorySample? Read(out string? error) { error = null; return null; } public void Dispose() { } }
    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void AllFiveRangeButtonsFitAndPaintDoesNotReproject(string language) => Sta(() =>
    {
        var old = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = language;
            using var form = new Form { ClientSize = new(540, 760) };
            var source = new Source();
            using var page = new NetworkPage(source, _ => true) { Dock = DockStyle.Fill };
            form.Controls.Add(page); form.Show(); Application.DoEvents(); page.Render();
            var chart = page.Chart;
            Assert.Equal(5, chart.RangeButtons.Count);
            for (var i = 0; i < 5; i++)
            {
                var button = chart.RangeButtons[i];
                Assert.True(button.Right <= chart.ClientSize.Width);
                Assert.True(button.Width >= TextRenderer.MeasureText(button.Text, button.Font).Width + 8 * chart.DeviceDpi / 96,
                    $"{button.Text}: width={button.Width}");
                button.PerformClick(); Assert.Equal(i, chart.RangeIndex);
            }
            var builds = chart.GeometryBuildCount;
            for (var i = 0; i < 10; i++) chart.Refresh();
            Assert.Equal(builds, chart.GeometryBuildCount);
            Assert.Equal(0, source.Starts); Assert.Equal(0, source.Refreshes);
            Assert.True(chart.PlotBounds(true).Bottom < chart.PlotBounds(false).Top);
            Assert.Equal(chart.PlotBounds(true).Left, chart.PlotBounds(false).Left);
        }
        finally { LocalizationService.CurrentLanguage = old; }
    });
    [Fact]
    public void MissingManualInterfaceRemainsSelected() => Sta(() =>
    {
        var source = new Source();
        var total = new DirectionTotal(null, null, null, false, null);
        var reading = new InterfaceReading("id", "Adapter", "Physical", null, new(null, null),
            new(total, total), new(null, null), [], 0, false);
        source.Current = new(1, "s", DateTimeOffset.UtcNow, "active", null, "id", [reading]);
        using var form = new Form { ClientSize = new(540, 760) };
        using var page = new NetworkPage(source, _ => true) { Dock = DockStyle.Fill };
        form.Controls.Add(page); form.Show(); Application.DoEvents(); page.Render();
        var choice = All(page).OfType<ComboBox>().Single(); choice.SelectedIndex = 1;
        Assert.Equal("id", page.Selection);
        source.Current = source.Current with { Version = 2, Interfaces = [], SystemInterfaceID = null };
        page.Render(); Assert.Equal("id", page.Selection);
        Assert.Contains("不可用", choice.Text);
    });
    [Fact]
    public void PageSwitchRefreshAndQuotaAccessibilityUseRealProductionPaths() => Sta(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "obdim-networkui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = new Source();
            var settings = new MonitoringSettingsService(Path.Combine(dir, "settings.json"));
            settings.Save(new MonitoringSettings { MemoryEnabled = false, Providers = [new() { Id = ProviderId.Codex, Enabled = true }] });
            using var coordinator = new MonitoringCoordinator(SystemClock.Instance, new Reader(),
                new Dictionary<ProviderId, IProviderAdapter>(), settings, new MonitoringCacheService(Path.Combine(dir, "cache")), network: source);
            using var form = new MonitorForm(coordinator);
            form.Show(); Application.DoEvents();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var cards = (Dictionary<ProviderId, Panel>)typeof(MonitorForm).GetField("_cards", flags)!.GetValue(form)!;
            foreach (var card in cards.Values) Assert.EndsWith("quota card", card.AccessibilityObject.Name);
            form.SetView(MonitorForm.View.Network); Application.DoEvents();
            var network = All(form).OfType<NetworkPage>().Single();
            source.SetEnabled(true); network.Render();
            typeof(MonitorForm).GetMethod("RequestRefreshForVisibleView", flags)!.Invoke(form, null);
            Assert.Equal(1, source.Refreshes);
            for (var i = 0; i < 20; i++)
            { form.SetView(MonitorForm.View.Memory); form.SetView(MonitorForm.View.Network); }
            Assert.Same(network, All(form).OfType<NetworkPage>().Single());
            Assert.Equal(1, source.Starts);
            Assert.Equal(1, source.Refreshes);
            network.Chart.Focus(); Application.DoEvents(); Assert.True(form.ChartOwnsKey(Keys.Left));
        }
        finally { Directory.Delete(dir, true); }
    });
}
