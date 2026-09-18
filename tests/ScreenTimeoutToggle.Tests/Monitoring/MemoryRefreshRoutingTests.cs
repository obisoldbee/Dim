using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// R30 / A06 (upgraded): the history must actually hold two hours at the sampling cadence, under
/// both bounds at once, and must announce a same-instant correction to anyone caching by version.
/// </summary>
public class MemoryHistoryBoundsTests
{
    /// <summary>Anchored per test and kept a few seconds inside the boundary; Trim() reads the real clock.</summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static MemorySample SampleAt(
        TimeSpan age,
        ulong available = 4_000_000_000,
        ulong total = 16_000_000_000) => new()
        {
            SampledAtUtc = Now - age,
            PhysicalTotalBytes = total,
            PhysicalAvailableBytes = available,
            CommitTotalBytes = 8_000_000_000,
            CommitLimitBytes = 32_000_000_000,
            LowMemorySignal = false,
        };

    /// <summary>
    /// The pairing that matters: five ranges topped by two hours, sampled every 5 seconds, needs
    /// 1441 points with both bounds inclusive. A 720/1-hour buffer cannot serve the fifth button no
    /// matter what the UI says, so range list and buffer bound are pinned together.
    /// </summary>
    [Fact]
    public void Defaults_CoverTheLongestSelectableRangeAtTheSamplingCadence()
    {
        Assert.Equal(TimeSpan.FromHours(2), MemoryHistoryBuffer.DefaultRetention);
        Assert.Equal(1600, MemoryHistoryBuffer.DefaultCapacity);

        var pointsInTwoHoursAtFiveSeconds = (int)(TimeSpan.FromHours(2).TotalSeconds / 5) + 1;
        Assert.Equal(1441, pointsInTwoHoursAtFiveSeconds);
        Assert.True(MemoryHistoryBuffer.DefaultCapacity > pointsInTwoHoursAtFiveSeconds,
            "headroom, so one early read does not evict the oldest sample of the 2-hour window");
    }

    /// <summary>
    /// The buffer is append-ordered — one sampler, monotone timestamps — and trimming relies on that,
    /// so these fixtures are built oldest first like the real feed.
    /// </summary>
    [Fact]
    public void SampleJustInsideRetention_IsKept_JustOutside_IsDropped()
    {
        var buffer = new MemoryHistoryBuffer();
        buffer.Add(SampleAt(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)));
        buffer.Add(SampleAt(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1)));

        var samples = buffer.SnapshotWithVersion().Samples;

        var only = Assert.Single(samples);
        Assert.True(only.SampledAtUtc > Now - TimeSpan.FromHours(2));
    }

    /// <summary>
    /// Two full hours of 5-second samples must survive on the default buffer. The oldest point sits a
    /// cadence inside the boundary rather than exactly on it, because Trim() compares against the
    /// wall clock at call time; the boundary itself is the test above.
    /// </summary>
    [Fact]
    public void TwoHoursOfFiveSecondSamples_AllFitWithoutEviction()
    {
        var buffer = new MemoryHistoryBuffer();
        for (var i = 1439; i >= 0; i--) buffer.Add(SampleAt(TimeSpan.FromSeconds(i * 5)));

        Assert.Equal(1440, buffer.Count);
    }

    /// <summary>Retention alone does not bound growth: the count limit has to hold as well.</summary>
    [Fact]
    public void BeyondCapacity_OldestAreEvictedEvenInsideRetention()
    {
        var buffer = new MemoryHistoryBuffer();
        for (var i = 1999; i >= 0; i--) buffer.Add(SampleAt(TimeSpan.FromSeconds(i)));

        Assert.Equal(1600, buffer.Count);
        var snapshot = buffer.SnapshotWithVersion().Samples;
        Assert.Equal(Now - TimeSpan.FromSeconds(1599), snapshot[0].SampledAtUtc);
        Assert.Equal(Now, snapshot[^1].SampledAtUtc);
    }

    /// <summary>
    /// A correction at an instant already in the history replaces that sample. Count must not grow —
    /// otherwise a flapping reader eats slots — and the value must actually change.
    /// </summary>
    [Fact]
    public void SameTimestampReplacement_ReplacesInPlaceAndBumpsVersion()
    {
        var buffer = new MemoryHistoryBuffer();
        buffer.Add(SampleAt(TimeSpan.FromMinutes(5), available: 4_000_000_000));
        var before = buffer.SnapshotWithVersion();

        buffer.Add(before.Samples[0] with { PhysicalAvailableBytes = 2_000_000_000 });
        var after = buffer.SnapshotWithVersion();

        var only = Assert.Single(after.Samples);
        Assert.Equal(2_000_000_000UL, only.PhysicalAvailableBytes);
        Assert.True(after.Version > before.Version,
            "count and newest timestamp are unchanged here, so version is the only signal a cache has");
    }

    [Fact]
    public void IdenticalResample_LeavesVersionAlone()
    {
        var buffer = new MemoryHistoryBuffer();
        var sample = SampleAt(TimeSpan.FromMinutes(1));
        buffer.Add(sample);
        var first = buffer.Version;

        buffer.Add(sample);

        Assert.Equal(first, buffer.Version);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void VersionAdvancesOnNewSampleAndOnClear()
    {
        var buffer = new MemoryHistoryBuffer();
        buffer.Add(SampleAt(TimeSpan.FromMinutes(2)));
        var afterAdd = buffer.Version;

        buffer.Add(SampleAt(TimeSpan.FromMinutes(1)));
        var afterSecondAdd = buffer.Version;
        buffer.Clear();

        Assert.True(afterSecondAdd > afterAdd);
        Assert.True(buffer.Version > afterSecondAdd);
        Assert.Equal(0, buffer.Count);
    }

    /// <summary>Clearing an empty buffer is not a content change and must not invalidate caches.</summary>
    [Fact]
    public void Clear_WhenAlreadyEmpty_KeepsVersion()
    {
        var buffer = new MemoryHistoryBuffer();
        var before = buffer.Version;

        buffer.Clear();

        Assert.Equal(before, buffer.Version);
    }

    [Fact]
    public void Snapshot_IsACopy_ExternalMutationCannotRewriteHistory()
    {
        var buffer = new MemoryHistoryBuffer();
        buffer.Add(SampleAt(TimeSpan.FromMinutes(1)));

        var snapshot = buffer.SnapshotWithVersion();
        var copied = new List<MemorySample>(snapshot.Samples) { SampleAt(TimeSpan.FromMinutes(2)) };

        Assert.Equal(2, copied.Count);
        Assert.Single(buffer.SnapshotWithVersion().Samples);
    }
}

/// <summary>
/// R31 / AM04: the refresh button on the memory page requests one memory sample and nothing else.
/// A page whose refresh shells out to three provider CLIs is both slower and a breach of the quota
/// page's "仅手动" contract.
/// </summary>
public class MemoryRefreshRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"obdim-memref-{Guid.NewGuid():N}");
    private readonly FakeClock _clock = new();

    public MemoryRefreshRoutingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class StubMemoryReader : IMemoryReader
    {
        private readonly int _succeedFirst;
        private readonly TimeSpan _delay;
        private int _reads;

        public StubMemoryReader(int succeedFirst = int.MaxValue, TimeSpan? delay = null)
        {
            _succeedFirst = succeedFirst;
            _delay = delay ?? TimeSpan.Zero;
        }

        public int Reads => Volatile.Read(ref _reads);

        public MemorySample? Read(out string? error)
        {
            var attempt = Interlocked.Increment(ref _reads);
            if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);

            if (attempt > _succeedFirst)
            {
                error = "simulated read failure";
                return null;
            }

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

    private sealed class FailFastAdapter : IProviderAdapter
    {
        private int _queries;

        public ProviderId Id { get; init; }

        public int Queries => Volatile.Read(ref _queries);

        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _queries);
            return Task.FromException<ProviderSnapshot>(
                new InvalidOperationException("the memory page must not query a quota provider"));
        }
    }

    private (MonitoringCoordinator Coordinator, StubMemoryReader Reader, Dictionary<ProviderId, IProviderAdapter> Adapters)
        Build(MonitoringSettings settings, StubMemoryReader reader)
    {
        var adapters = Enum.GetValues<ProviderId>()
            .ToDictionary(id => id, id => (IProviderAdapter)new FailFastAdapter { Id = id });
        var coordinator = new MonitoringCoordinator(
            _clock,
            reader,
            adapters,
            new MonitoringSettingsService(Path.Combine(_dir, $"monitoring-{Guid.NewGuid():N}.json")),
            new MonitoringCacheService(Path.Combine(_dir, $"cache-{Guid.NewGuid():N}")),
            new MemoryHistoryBuffer());
        coordinator.ApplySettings(settings);
        return (coordinator, reader, adapters);
    }

    /// <summary>
    /// Providers are switched off for the memory cases on purpose: enabling a provider through
    /// ApplySettings is itself a user action that queries it once (the "首次启用立即查询" rule), and
    /// that baseline read is not the memory page's doing. What must hold is that a memory refresh
    /// adds nothing on its own — which is only observable against a quiet starting point.
    /// </summary>
    private static MonitoringSettings Settings(bool memoryEnabled, bool providersEnabled = false) => new()
    {
        MemoryEnabled = memoryEnabled,
        Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
        {
            Id = id,
            Enabled = providersEnabled,
        }).ToList(),
    };

    [Fact]
    public void RequestMemoryRefresh_ReadsMemoryOnce_AndNoProvider()
    {
        var reader = new StubMemoryReader();
        var (coordinator, _, adapters) = Build(Settings(true), reader);
        using (coordinator)
        {
            Assert.True(coordinator.RequestMemoryRefresh());
            SpinWait.SpinUntil(() => coordinator.MemoryHistory.Count > 0, TimeSpan.FromSeconds(5));

            Assert.Equal(1, reader.Reads);
            Assert.All(adapters.Values, a => Assert.Equal(0, ((FailFastAdapter)a).Queries));
            Assert.NotNull(coordinator.LatestMemorySample);
            Assert.Null(coordinator.LastMemoryError);
            Assert.False(coordinator.MemoryRefreshInFlight);
        }
    }

    [Fact]
    public void RequestMemoryRefresh_WhenDisabled_ReadsNothingAndReportsFalse()
    {
        var reader = new StubMemoryReader();
        var (coordinator, _, _) = Build(Settings(false), reader);
        using (coordinator)
        {
            Assert.False(coordinator.RequestMemoryRefresh());
            Thread.Sleep(200);

            Assert.Equal(0, reader.Reads);
            Assert.Equal(0, coordinator.MemoryHistory.Count);
            Assert.Null(coordinator.LatestMemorySample);
        }
    }

    /// <summary>
    /// Single flight: while a read is running a second request is coalesced, not queued. Two
    /// overlapping reads of the same instant would add a duplicate to the history for no
    /// informational gain, and would make the button's feedback dishonest.
    /// </summary>
    [Fact]
    public void RequestMemoryRefresh_WhileOneIsRunning_IsCoalesced()
    {
        var reader = new StubMemoryReader(delay: TimeSpan.FromMilliseconds(600));
        var (coordinator, _, _) = Build(Settings(true), reader);
        using (coordinator)
        {
            Assert.True(coordinator.RequestMemoryRefresh());
            SpinWait.SpinUntil(() => reader.Reads == 1, TimeSpan.FromSeconds(2));
            Assert.True(coordinator.MemoryRefreshInFlight);

            Assert.False(coordinator.RequestMemoryRefresh());

            SpinWait.SpinUntil(() => !coordinator.MemoryRefreshInFlight, TimeSpan.FromSeconds(5));
            Assert.Equal(1, reader.Reads);
        }
    }

    /// <summary>
    /// A failed read must not clear the last good figures or grow the history; the page keeps the
    /// previous sample and lets the freshness rule mark it stale.
    /// </summary>
    [Fact]
    public void RequestMemoryRefresh_OnFailure_KeepsLastSample_AndRecordsError()
    {
        var reader = new StubMemoryReader(succeedFirst: 1);
        var (coordinator, _, _) = Build(Settings(true), reader);
        using (coordinator)
        {
            Assert.True(coordinator.RequestMemoryRefresh());
            SpinWait.SpinUntil(() => coordinator.MemoryHistory.Count == 1, TimeSpan.FromSeconds(5));
            var firstSample = coordinator.LatestMemorySample;
            Assert.NotNull(firstSample);

            Assert.True(coordinator.RequestMemoryRefresh());
            SpinWait.SpinUntil(() => coordinator.LastMemoryError is not null, TimeSpan.FromSeconds(5));

            Assert.Equal(2, reader.Reads);
            Assert.Equal(firstSample, coordinator.LatestMemorySample);
            Assert.Equal(1, coordinator.MemoryHistory.Count);
            Assert.NotNull(coordinator.LastMemoryError);
        }
    }

    [Fact]
    public void RequestMemoryRefresh_AfterDispose_IsRejectedWithoutReading()
    {
        var reader = new StubMemoryReader();
        var (coordinator, _, _) = Build(Settings(true), reader);
        coordinator.Dispose();

        Assert.False(coordinator.RequestMemoryRefresh());
        Thread.Sleep(150);
        Assert.Equal(0, reader.Reads);
    }

    /// <summary>
    /// Scoping the memory page's button must not have narrowed the quota page's: an enabled provider
    /// still receives its refresh.
    /// </summary>
    [Fact]
    public void RequestManualRefreshAll_StillTargetsEnabledProviders()
    {
        var reader = new StubMemoryReader();
        var (coordinator, _, adapters) = Build(Settings(true, providersEnabled: true), reader);
        using (coordinator)
        {
            coordinator.RequestManualRefreshAll();
            SpinWait.SpinUntil(
                () => adapters.Values.Cast<FailFastAdapter>().Any(a => a.Queries > 0),
                TimeSpan.FromSeconds(3));

            Assert.Contains(adapters.Values.Cast<FailFastAdapter>(), a => a.Queries > 0);
            Assert.Equal(0, reader.Reads);
        }
    }
}
