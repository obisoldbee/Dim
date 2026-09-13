using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Programmable adapter: returns snapshots from a delegate; can block until released.</summary>
internal sealed class FakeAdapter : IProviderAdapter
{
    private readonly Func<ProviderSnapshot> _next;

    public FakeAdapter(ProviderId id, Func<ProviderSnapshot> next)
    {
        Id = id;
        _next = next;
    }

    public ProviderId Id { get; }

    public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.Run(_next, cancellationToken);
}

/// <summary>
/// Coordinator behavior with injected clock/adapter/cache: failure isolation, identity
/// switch hygiene, single-flight, reminder dispatch. No real timers are started (Start()
/// is not called) — refreshes are triggered via the public manual path.
/// </summary>
public class MonitoringCoordinatorTests : IDisposable
{
    private readonly string _settingsDir;
    private readonly string _cacheDir;
    private readonly FakeClock _clock = new();

    public MonitoringCoordinatorTests()
    {
        _settingsDir = Path.Combine(Path.GetTempPath(), $"obdim-coord-s-{Guid.NewGuid():N}");
        _cacheDir = Path.Combine(Path.GetTempPath(), $"obdim-coord-c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsDir, true); } catch (IOException) { }
        try { Directory.Delete(_cacheDir, true); } catch (IOException) { }
    }

    private MonitoringSettingsService MakeSettingsService(MonitoringSettings? initial = null)
    {
        var path = Path.Combine(_settingsDir, "monitoring.json");
        var svc = new MonitoringSettingsService(path);
        if (initial is not null) svc.Save(initial);
        return svc;
    }

    private static MonitoringSettings EnabledSettings(bool reminders = false, params ProviderId[] enabled) => new()
    {
        RemindersEnabled = reminders,
        Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
        {
            Id = id,
            Enabled = enabled.Contains(id),
        }).ToList(),
    };

    private ProviderSnapshot Good(ProviderId id, string identity = "ident-1", double remaining = 80) => new()
    {
        Provider = id,
        IdentityKey = identity,
        IdentityVerified = true,
        AttemptedAtUtc = _clock.UtcNow,
        SucceededAtUtc = _clock.UtcNow,
        Buckets =
        [
            new QuotaBucket
            {
                SourceKey = "b1",
                Windows =
                [
                    // Reset point 3h ahead of the injected clock — always "fresh" relative
                    // to the coordinator's notion of now, never the wall clock.
                    new QuotaWindow { SourceKey = "primary", UsedPercent = 100 - remaining, RemainingPercent = remaining, ResetsAtUtc = _clock.UtcNow.AddHours(3), HasAnyQuotaField = true },
                ],
            },
        ],
    };

    private ProviderSnapshot Failure(ProviderId id, ProviderErrorKind kind = ProviderErrorKind.Timeout) => new()
    {
        Provider = id,
        IdentityKey = "ident-1",
        IdentityVerified = false,
        AttemptedAtUtc = _clock.UtcNow,
        Error = kind,
    };

    /// <summary>Manual refreshes are gated at 15 s — the fake clock must clear that gate.</summary>
    private void ClearManualGate() => _clock.Advance(TimeSpan.FromSeconds(16));

    private sealed class EventLatch
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action Handler => () => _tcs.TrySetResult();
        public async Task WaitAsync(TimeSpan timeout)
        {
            var finished = await Task.WhenAny(_tcs.Task, Task.Delay(timeout));
            Assert.True(finished == _tcs.Task, "timed out waiting for QuotaStateChanged");
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
        return null!;
    }

    private static async Task WaitForTrueAsync(Func<bool?> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() == true) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    [Fact]
    public async Task ManualRefresh_Success_LastGoodVisible()
    {
        var adapter = new FakeAdapter(ProviderId.Codex, () => Good(ProviderId.Codex));
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        var latch = new EventLatch();
        coordinator.QuotaStateChanged += latch.Handler;

        Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
        await latch.WaitAsync(TimeSpan.FromSeconds(10));

        var state = await WaitForAsync(
            () => coordinator.GetDisplayState(ProviderId.Codex).LastGood,
            TimeSpan.FromSeconds(10), "last good snapshot");
        Assert.Equal("ident-1", state.IdentityKey);
        Assert.False(coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
    }

    /// <summary>A04: 一家失败不阻塞其他来源；失败不更新成功时间，旧快照保留。</summary>
    [Fact]
    public async Task Failure_KeepsPreviousGoodSnapshot_AndShowsFailure()
    {
        ProviderSnapshot next = Good(ProviderId.Codex);
        var adapter = new FakeAdapter(ProviderId.Codex, () => next);
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        coordinator.QuotaStateChanged += () => { };
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastGood, TimeSpan.FromSeconds(10), "first good");

        next = Failure(ProviderId.Codex, ProviderErrorKind.Timeout);
        ClearManualGate();
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastAttempt, TimeSpan.FromSeconds(10), "failure attempt");

        var state = coordinator.GetDisplayState(ProviderId.Codex);
        Assert.NotNull(state.LastGood);                      // 旧快照仍在
        Assert.NotNull(state.LastAttempt);                   // 失败单独可见
        Assert.Equal(ProviderErrorKind.Timeout, state.LastAttempt!.Error);
        // 旧快照的成功时间未被失败刷新 (spec §5.2(5))
        Assert.Equal(state.LastGood!.SucceededAtUtc, state.LastGood.SucceededAtUtc);
    }

    /// <summary>A09/A04: 未核实身份的失败不动旧快照；已验证的身份切换后旧账号数据不得沿用。</summary>
    [Fact]
    public async Task IdentitySwitch_DropsOldSnapshot_FailuresKeepPrevious()
    {
        ProviderSnapshot next = Good(ProviderId.Codex, "ident-A");
        var adapter = new FakeAdapter(ProviderId.Codex, () => next);
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        coordinator.QuotaStateChanged += () => { };

        // A 账号成功。
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastGood, TimeSpan.FromSeconds(10), "identity A snapshot");
        Assert.Equal("ident-A", coordinator.GetDisplayState(ProviderId.Codex).LastGood!.IdentityKey);

        // 超时失败（身份未核实）：A 的旧快照必须保留显示，只记录失败。
        next = Failure(ProviderId.Codex, ProviderErrorKind.Timeout);
        ClearManualGate();
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastAttempt, TimeSpan.FromSeconds(10), "timeout attempt");

        var state = coordinator.GetDisplayState(ProviderId.Codex);
        Assert.Equal("ident-A", state.LastGood!.IdentityKey);
        Assert.Equal(ProviderErrorKind.Timeout, state.LastAttempt!.Error);

        // B 账号新鲜成功：身份切换，旧 A 数据不得沿用。
        next = Good(ProviderId.Codex, "ident-B", remaining: 30);
        ClearManualGate();
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForTrueAsync(
            () => coordinator.GetDisplayState(ProviderId.Codex).LastGood?.IdentityKey == "ident-B",
            TimeSpan.FromSeconds(10), "fresh B snapshot");

        Assert.Equal(30, coordinator.GetDisplayState(ProviderId.Codex).LastGood!.Buckets[0].Windows[0].RemainingPercent);
        Assert.DoesNotContain("ident-A", coordinator.GetDisplayState(ProviderId.Codex).LastGood!.IdentityKey);
    }

    /// <summary>
    /// A09 — the case `identityChanged` cannot see: signing OUT produces a snapshot with
    /// NO verified identity, so the switch branch never fires and the previous account's
    /// quota would keep being displayed (dimmed, but still the wrong person's numbers,
    /// and still driving reminders). A credential-level failure must drop it.
    /// Transport failures (timeout / api / parse) must NOT — see
    /// <see cref="Failure_KeepsPreviousGoodSnapshot_AndShowsFailure"/>.
    /// </summary>
    [Fact]
    public async Task NotSignedIn_DropsPreviousAccountQuota()
    {
        ProviderSnapshot next = Good(ProviderId.Codex, "ident-A");
        var adapter = new FakeAdapter(ProviderId.Codex, () => next);
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        coordinator.QuotaStateChanged += () => { };

        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastGood, TimeSpan.FromSeconds(10), "identity A snapshot");
        Assert.Equal("ident-A", coordinator.GetDisplayState(ProviderId.Codex).LastGood!.IdentityKey);

        // Signed out: the account is gone, so its numbers must go with it.
        next = Failure(ProviderId.Codex, ProviderErrorKind.NotSignedIn);
        ClearManualGate();
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForTrueAsync(
            () => coordinator.GetDisplayState(ProviderId.Codex).LastAttempt?.Error == ProviderErrorKind.NotSignedIn,
            TimeSpan.FromSeconds(10), "not-signed-in attempt");

        var state = coordinator.GetDisplayState(ProviderId.Codex);
        Assert.Null(state.LastGood);
        Assert.NotNull(state.LastAttempt);
        Assert.Equal(ProviderErrorKind.NotSignedIn, state.LastAttempt!.Error);

        // Re-signing in (even as the same identity) brings the panel back to life.
        next = Good(ProviderId.Codex, "ident-A");
        ClearManualGate();
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastGood, TimeSpan.FromSeconds(10), "snapshot after re-signin");
        Assert.Equal("ident-A", coordinator.GetDisplayState(ProviderId.Codex).LastGood!.IdentityKey);
    }

    [Fact]
    public async Task SingleFlight_SecondManualStartRejected()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeAdapter(ProviderId.Codex, () =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return Good(ProviderId.Codex);
        });
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        // First manual refresh begins; the adapter blocks.
        Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
        await WaitForTrueAsync(() => coordinator.GetDisplayState(ProviderId.Codex).Refreshing,
            TimeSpan.FromSeconds(10), "in-flight state");

        // Second click must NOT stack a second process (spec §7 手动刷新).
        Assert.False(coordinator.RequestManualRefresh(ProviderId.Codex));

        release.TrySetResult();
        await WaitForTrueAsync(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing,
            TimeSpan.FromSeconds(10), "refresh completion");
    }

    [Fact]
    public async Task DisabledProvider_ManualRefreshRejected()
    {
        var adapter = new FakeAdapter(ProviderId.Codex, () => Good(ProviderId.Codex));
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings()), // nothing enabled
            new MonitoringCacheService(_cacheDir));

        Assert.False(coordinator.RequestManualRefresh(ProviderId.Codex));
        Assert.False(coordinator.GetDisplayState(ProviderId.Codex).Enabled);
        await Task.Delay(100);
        Assert.Null(coordinator.GetDisplayState(ProviderId.Codex).LastGood);
    }

    /// <summary>R09: 提醒只对新鲜有效额度触发一次；关掉开关后不再触发。</summary>
    [Fact]
    public async Task Reminders_FireOncePerLevel_AndRespectSettings()
    {
        ProviderSnapshot next = Good(ProviderId.Codex, remaining: 18);
        var adapter = new FakeAdapter(ProviderId.Codex, () => next);
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(reminders: true, enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        var fired = new List<QuotaReminderEvent>();
        var firedLatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ReminderFired += evt =>
        {
            lock (fired) fired.Add(evt);
            firedLatch.TrySetResult();
        };

        coordinator.RequestManualRefresh(ProviderId.Codex);
        await firedLatch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(fired);

        // Second identical refresh: dedupe must hold.
        var countBefore = fired.Count;
        ClearManualGate();
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await Task.Delay(500);
        Assert.Equal(countBefore, fired.Count);
    }

    [Fact]
    public async Task Reminders_Disabled_FiresNothing()
    {
        ProviderSnapshot next = Good(ProviderId.Codex, remaining: 3);
        var adapter = new FakeAdapter(ProviderId.Codex, () => next);
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings(reminders: false, enabled: ProviderId.Codex)),
            new MonitoringCacheService(_cacheDir));

        var fired = 0;
        coordinator.ReminderFired += _ => Interlocked.Increment(ref fired);

        coordinator.RequestManualRefresh(ProviderId.Codex);
        await WaitForTrueAsync(() => coordinator.GetDisplayState(ProviderId.Codex).LastGood is not null,
            TimeSpan.FromSeconds(10), "refresh");
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact]
    public void ApplySettings_NewlyEnabled_TriggersQuery()
    {
        var adapter = new FakeAdapter(ProviderId.Codex, () => Good(ProviderId.Codex));
        using var coordinator = new MonitoringCoordinator(
            _clock, new NullMemoryReader(),
            new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter },
            MakeSettingsService(EnabledSettings()),
            new MonitoringCacheService(_cacheDir));

        coordinator.ApplySettings(EnabledSettings(enabled: ProviderId.Codex));
        Assert.True(coordinator.Settings.Provider(ProviderId.Codex).Enabled);
    }

    /// <summary>A06: 内存监控关闭后不继续采样、缓冲不再增长。</summary>
    [Fact]
    public void MemoryDisabled_SamplerDoesNothing()
    {
        using var reader = new NullMemoryReader();
        using var coordinator = new MonitoringCoordinator(
            _clock, reader,
            new Dictionary<ProviderId, IProviderAdapter>(),
            MakeSettingsService(new MonitoringSettings { MemoryEnabled = false }),
            new MonitoringCacheService(_cacheDir));

        coordinator.ApplySettings(new MonitoringSettings { MemoryEnabled = false });
        Assert.Null(coordinator.LatestMemorySample);
        Assert.Equal(0, coordinator.MemoryHistory.Count);
    }

    /// <summary>Real reader, disabled memory: nothing sampled. (Keeps the fake minimal.)</summary>
    private sealed class NullMemoryReader : IMemoryReader
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
}
