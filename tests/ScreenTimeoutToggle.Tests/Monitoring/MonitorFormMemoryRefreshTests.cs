using System.Windows.Forms;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// R31 / AM04: the routing has to hold for the actual button, not only for the coordinator call.
/// The top-bar refresh used to be wired straight to RequestManualRefreshAll whatever the visible
/// page, so a click on the memory page launched up to three provider CLIs to redraw a chart that
/// needs one local read — and broke the quota page's "仅手动" promise from the other direction.
/// </summary>
[Collection("NativeUi")]
public class MonitorFormMemoryRefreshTests
{
    [Fact]
    public void RefreshButton_OnMemoryPage_ReadsMemoryWithoutTouchingProviders()
    {
        Run(() =>
        {
            using var harness = new Harness();
            using var form = new MonitorForm(harness.Coordinator);
            form.SetView(MonitorForm.View.Memory);
            Show(form);

            var before = harness.Reader.Reads;
            ClickRefresh(form);
            SpinWait.SpinUntil(() => harness.Reader.Reads > before, TimeSpan.FromSeconds(5));

            Assert.Equal(before + 1, harness.Reader.Reads);
            Assert.All(harness.Adapters.Values, a => Assert.Equal(0, ((RecordingAdapter)a).Queries));
        });
    }

    [Fact]
    public void RefreshButton_OnQuotaPage_StillRefreshesProvidersWithoutSamplingMemory()
    {
        Run(() =>
        {
            using var harness = new Harness();
            using var form = new MonitorForm(harness.Coordinator);
            form.SetView(MonitorForm.View.Quota);
            Show(form);

            var before = harness.Reader.Reads;
            ClickRefresh(form);
            SpinWait.SpinUntil(
                () => harness.Adapters.Values.Cast<RecordingAdapter>().Any(a => a.Queries > 0),
                TimeSpan.FromSeconds(5));

            Assert.Contains(harness.Adapters.Values.Cast<RecordingAdapter>(), a => a.Queries > 0);
            Assert.Equal(before, harness.Reader.Reads);
        });
    }

    /// <summary>
    /// With memory sampling off there is nothing for the button to do, and clicking it must not
    /// start a read anyway — the page says so rather than appearing to succeed.
    /// </summary>
    [Fact]
    public void RefreshButton_IsDisabled_WhenMemorySamplingIsOff()
    {
        Run(() =>
        {
            using var harness = new Harness(memoryEnabled: false);
            using var form = new MonitorForm(harness.Coordinator);
            form.SetView(MonitorForm.View.Memory);
            Show(form);

            var button = RefreshButton(form);
            Assert.False(button.Enabled);
            Assert.Equal(LocalizationService.Get("monitor.refresh_memory_off"), button.AccessibleName);
            Assert.Equal(0, harness.Reader.Reads);
        });
    }

    /// <summary>Owns the temporary settings/cache directory the coordinator writes into.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"obdim-memroute-{Guid.NewGuid():N}");

        public Harness(bool memoryEnabled = true)
        {
            Directory.CreateDirectory(_dir);
            Reader = new CountingReader();
            Adapters = Enum.GetValues<ProviderId>()
                .ToDictionary(id => id, id => (IProviderAdapter)new RecordingAdapter { Id = id });

            var settings = new MonitoringSettingsService(Path.Combine(_dir, "monitoring.json"));
            settings.Save(new MonitoringSettings
            {
                MemoryEnabled = memoryEnabled,
                Providers = Enum.GetValues<ProviderId>()
                    .Select(id => new ProviderSettings { Id = id, Enabled = true }).ToList(),
            });

            Coordinator = new MonitoringCoordinator(
                new FakeClock(),
                Reader,
                Adapters,
                settings,
                new MonitoringCacheService(Path.Combine(_dir, "cache")),
                new MemoryHistoryBuffer());
        }

        public CountingReader Reader { get; }
        public Dictionary<ProviderId, IProviderAdapter> Adapters { get; }
        public MonitoringCoordinator Coordinator { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private static void Show(MonitorForm form)
    {
        form.Show();
        Application.DoEvents();
    }

    private static void ClickRefresh(Control form) => RefreshButton(form).PerformClick();

    private static Button RefreshButton(Control form)
    {
        var button = Descendants(form).OfType<Button>().FirstOrDefault(b => b.Text == "⟳");
        Assert.True(button is not null, "refresh button not found");
        return button!;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class CountingReader : IMemoryReader
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public MemorySample? Read(out string? error)
        {
            Interlocked.Increment(ref _reads);
            error = null;
            return new MemorySample
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                PhysicalTotalBytes = 16_000_000_000,
                PhysicalAvailableBytes = 6_000_000_000,
                CommitTotalBytes = 9_000_000_000,
                CommitLimitBytes = 32_000_000_000,
                LowMemorySignal = false,
            };
        }

        public void Dispose() { }
    }

    private sealed class RecordingAdapter : IProviderAdapter
    {
        private int _queries;
        public ProviderId Id { get; init; }
        public int Queries => Volatile.Read(ref _queries);

        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _queries);
            return Task.FromException<ProviderSnapshot>(new InvalidOperationException("not in this test"));
        }
    }

    private static void Run(Action body)
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
