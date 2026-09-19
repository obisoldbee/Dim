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
/// A provider the user switched off has nothing to report, and the card it used to keep on screen
/// cost both space and work: a measured status line, a sized card, a row pass. It is now absent,
/// with one page-level line standing in when nothing is enabled at all.
/// </summary>
[Collection("NativeUi")]
public class DisabledProviderCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"obdim-discard-{Guid.NewGuid():N}");

    public DisabledProviderCardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void DisabledProvider_RendersNoCard()
    {
        Run(() =>
        {
            using var harness = new Harness(_dir, enabled: [ProviderId.Codex]);
            using var form = Open(harness);

            Assert.True(CardOf(form, ProviderId.Codex).Visible, "an enabled provider must still render");
            foreach (var id in new[] { ProviderId.MiniMax, ProviderId.Ark })
            {
                Assert.False(CardOf(form, id).Visible, $"{id} is switched off and should not be on screen");
            }
        });
    }

    /// <summary>
    /// The saving is not only visual: no row controls are built for a switched-off provider, so a
    /// page with two of three sources off does no layout work for them.
    /// </summary>
    [Fact]
    public void DisabledProvider_BuildsNoRows()
    {
        Run(() =>
        {
            using var harness = new Harness(_dir, enabled: [ProviderId.Codex]);
            using var form = Open(harness);

            Assert.Equal(1, harness.Queries(ProviderId.Codex));
            Assert.Equal(0, harness.Queries(ProviderId.MiniMax));
            Assert.Equal(0, harness.Queries(ProviderId.Ark));

            // The hidden card holds no metric rows; the visible one has its window row.
            Assert.Empty(RowsPanelOf(CardOf(form, ProviderId.Ark)).Controls);
            Assert.NotEmpty(RowsPanelOf(CardOf(form, ProviderId.Codex)).Controls);
        });
    }

    [Fact]
    public void AllProvidersDisabled_ShowsOneHintInsteadOfThreeCards()
    {
        Run(() =>
        {
            using var harness = new Harness(_dir, enabled: []);
            using var form = Open(harness);

            Assert.All(Enum.GetValues<ProviderId>(), id => Assert.False(CardOf(form, id).Visible));

            var hint = Hint(form);
            Assert.True(hint.Visible, "a blank page with no explanation is not a substitute");
            Assert.Equal(LocalizationService.Get("monitor.quota_all_disabled"), hint.Text);
        });
    }

    /// <summary>Turning a source back on has to bring its card back without rebuilding the page.</summary>
    [Fact]
    public void EnablingAProvider_Later_BringsItsCardBack()
    {
        Run(() =>
        {
            using var harness = new Harness(_dir, enabled: []);
            using var form = Open(harness);
            Assert.False(CardOf(form, ProviderId.Ark).Visible);
            Assert.True(Hint(form).Visible);

            harness.SetEnabled([ProviderId.Ark]);
            SpinWait.SpinUntil(() => harness.Queries(ProviderId.Ark) > 0, TimeSpan.FromSeconds(5));
            Application.DoEvents();

            Assert.True(CardOf(form, ProviderId.Ark).Visible);
            Assert.False(Hint(form).Visible);
        });
    }

    /// <summary>
    /// The discriminating case for "renders nothing": a provider that was on, fetched data, and
    /// was then switched off. It still holds a last-good snapshot, so without the early return the
    /// page keeps building and sizing its rows for a source the user turned off.
    /// </summary>
    [Fact]
    public void ProviderDisabledAfterFetchingData_DropsCardAndRows()
    {
        Run(() =>
        {
            using var harness = new Harness(_dir, enabled: [ProviderId.Codex]);
            using var form = Open(harness);
            var card = CardOf(form, ProviderId.Codex);
            Assert.True(card.Visible);
            Assert.NotEmpty(RowsPanelOf(card).Controls);

            harness.SetEnabled([]);
            Application.DoEvents();

            Assert.False(card.Visible, "the card must go when the source is switched off");
            Assert.Empty(RowsPanelOf(card).Controls);
            Assert.True(Hint(form).Visible);
        });
    }

    // ---------- helpers ----------

    private static MonitorForm Open(Harness harness)
    {
        var form = new MonitorForm(harness.Coordinator);
        form.SetView(MonitorForm.View.Quota);
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static Control CardOf(Control form, ProviderId id)
    {
        var status = Descendants<LinkLabel>(form).SingleOrDefault(l => l.Name == $"providerStatus_{id}");
        Assert.True(status?.Parent is not null, $"no card found for {id}");
        return status!.Parent!;
    }

    private static Label Hint(Control form)
    {
        var hint = Descendants<Label>(form).SingleOrDefault(l => l.Name == "quotaAllDisabledHint");
        Assert.NotNull(hint);
        return hint!;
    }

    private static FlowLayoutPanel RowsPanelOf(Control card)
    {
        var panel = card.Controls.OfType<FlowLayoutPanel>().SingleOrDefault();
        Assert.NotNull(panel);
        return panel!;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control =>
        Descendants(root).OfType<T>();

    private sealed class Harness : IDisposable
    {
        private readonly FakeClock _clock = new();

        public Harness(string parentDir, ProviderId[] enabled)
        {
            var dir = Path.Combine(parentDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _path = dir;
            Adapters = Enum.GetValues<ProviderId>()
                .ToDictionary(id => id, id => (IProviderAdapter)new StubAdapter(id, _clock));

            SettingsService = new MonitoringSettingsService(Path.Combine(dir, "monitoring.json"));
            SettingsService.Save(SettingsFor(enabled));
            Coordinator = new MonitoringCoordinator(
                _clock, new StubMemoryReader(), Adapters, SettingsService,
                new MonitoringCacheService(Path.Combine(dir, "cache")));
            SetEnabled(enabled);
        }

        private string _path = "";
        public Dictionary<ProviderId, IProviderAdapter> Adapters { get; }

        /// <summary>How many times a provider's adapter was actually asked to query.</summary>
        public int Queries(ProviderId id) => ((StubAdapter)Adapters[id]).Queries;
        public MonitoringCoordinator Coordinator { get; }
        private MonitoringSettingsService SettingsService { get; }

        private static MonitoringSettings SettingsFor(ProviderId[] enabled) => new()
        {
            MemoryEnabled = false,
            Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
            {
                Id = id,
                Enabled = enabled.Contains(id),
            }).ToList(),
        };

        /// <summary>Applies through the coordinator, the same route the settings window uses.</summary>
        public void SetEnabled(ProviderId[] enabled)
        {
            Coordinator.ApplySettings(SettingsFor(enabled));
            foreach (var id in enabled) Coordinator.RequestManualRefresh(id);
            Application.DoEvents();
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            try { Directory.Delete(_path, true); } catch (IOException) { }
        }
    }

    private sealed class StubAdapter(ProviderId id, FakeClock clock) : IProviderAdapter
    {
        public int Queries;
        public ProviderId Id { get; } = id;

        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Queries);
            return Task.FromResult(new ProviderSnapshot
            {
                Provider = Id,
                IdentityKey = $"identity-{Id}",
                IdentityVerified = true,
                AttemptedAtUtc = clock.UtcNow,
                SucceededAtUtc = clock.UtcNow,
                Buckets =
                [
                    new QuotaBucket
                    {
                        SourceKey = "primary",
                        Windows =
                        [
                            new QuotaWindow
                            {
                                SourceKey = "weekly", UsedPercent = 44, RemainingPercent = 56,
                                ResetsAtUtc = clock.UtcNow.AddHours(5), HasAnyQuotaField = true,
                            },
                        ],
                    },
                ],
            });
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
                PhysicalAvailableBytes = 6_000_000_000,
                CommitTotalBytes = 9_000_000_000,
                CommitLimitBytes = 32_000_000_000,
            };
        }

        public void Dispose() { }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "the STA test thread did not finish in time");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("Assertion failed on the STA thread: " + failure.Message, failure);
        }
    }
}
