using System.Diagnostics;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

internal sealed class Round2Adapter(Func<ProviderSettings, CancellationToken, Task<ProviderSnapshot>> query) : IProviderAdapter
{
    public ProviderId Id => ProviderId.Codex;
    public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken) => query(settings, cancellationToken);
}
internal sealed class Round2Memory : IMemoryReader
{
    public MemorySample? Read(out string? error)
    {
        error = null;
        return new MemorySample { SampledAtUtc = DateTimeOffset.UtcNow, PhysicalTotalBytes = 100,
            PhysicalAvailableBytes = 40, CommitTotalBytes = 20, CommitLimitBytes = 200, LowMemorySignal = false };
    }
    public void Dispose() { }
}
internal sealed class Round2Environment : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "obdim-round2-" + Guid.NewGuid().ToString("N"));
    public FakeClock Clock { get; } = new() { UtcNow = DateTimeOffset.UtcNow };
    public MonitoringSettingsService Settings { get; }
    public MonitoringCacheService Cache { get; }
    public Round2Environment()
    {
        Directory.CreateDirectory(DirectoryPath);
        Settings = new(Path.Combine(DirectoryPath, "monitoring.json"));
        Cache = new(Path.Combine(DirectoryPath, "cache"));
    }
    public MonitoringSettings Enabled(int minutes = 0, string? path = null) => new()
    {
        RefreshIntervalMinutes = minutes,
        Providers = [new ProviderSettings { Id = ProviderId.Codex, Enabled = true, CliPath = path }],
    };
    public MonitoringCoordinator Create(IProviderAdapter adapter, MonitoringSettings? settings = null, IMonitoringCache? cache = null)
    {
        Settings.Save(settings ?? Enabled());
        return new(Clock, new Round2Memory(), new Dictionary<ProviderId, IProviderAdapter> { [ProviderId.Codex] = adapter }, Settings, cache ?? Cache);
    }
    public ProviderSnapshot Good(string identity = "account", double remaining = 65) => new()
    {
        Provider = ProviderId.Codex, IdentityKey = identity, IdentityVerified = true,
        AttemptedAtUtc = Clock.UtcNow, SucceededAtUtc = Clock.UtcNow,
        Buckets = [new QuotaBucket { SourceKey = "codex", Windows = [new QuotaWindow
        { SourceKey = "primary", HasAnyQuotaField = true, RemainingPercent = remaining, UsedPercent = 100-remaining,
            WindowDurationMinutes = 300, ResetsAtUtc = Clock.UtcNow.AddHours(3) }] }],
    };
    public static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "condition did not settle");
            await Task.Delay(10);
        }
    }
    public void Dispose() { try { Directory.Delete(DirectoryPath, true); } catch (IOException) { } }
}

public class Round2CoordinatorTests
{
    [Fact]
    public async Task ManualRefresh_SynchronousCliPreamble_DoesNotRunOnCaller()
    {
        using var env = new Round2Environment();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        var worker = caller;
        var adapter = new Round2Adapter((_, _) =>
        {
            worker = Environment.CurrentManagedThreadId;
            started.Set(); release.Wait(TimeSpan.FromSeconds(3));
            return Task.FromResult(env.Good());
        });
        using var coordinator = env.Create(adapter);
        try
        {
            var watch = Stopwatch.StartNew();
            Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "manual request waited for synchronous CLI startup");
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            Assert.NotEqual(caller, worker);
            Assert.True(coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        }
        finally { release.Set(); }
        await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
    }

    private sealed class BlockingCache : IMonitoringCache
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly ManualResetEventSlim Release = new();
        public CachedProviderState? Load(ProviderId provider, string identityKey)
        { Started.Set(); Release.Wait(TimeSpan.FromSeconds(5)); return null; }
        public bool Save(CachedProviderState state) => true;
        public void ClearProvider(ProviderId provider) { }
    }

    [Fact]
    public async Task SlowCacheRead_DoesNotHoldStateLock_AndCannotPublishAfterDisable()
    {
        using var env = new Round2Environment();
        var cache = new BlockingCache();
        using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good(remaining: 2))), env.Enabled() with { RemindersEnabled = true }, cache);
        var reminders = 0;
        coordinator.ReminderFired += _ => Interlocked.Increment(ref reminders);
        try
        {
            coordinator.RequestManualRefresh(ProviderId.Codex);
            Assert.True(cache.Started.Wait(TimeSpan.FromSeconds(2)));
            var read = Task.Run(() => coordinator.GetDisplayState(ProviderId.Codex));
            await read.WaitAsync(TimeSpan.FromSeconds(1));
            var disable = Task.Run(() => coordinator.ApplySettings(new MonitoringSettings()));
            await disable.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { cache.Release.Set(); }
        await Task.Delay(100);
        Assert.Null(coordinator.GetDisplayState(ProviderId.Codex).LastGood);
        Assert.Equal(0, reminders);
    }

    [Fact]
    public async Task PathChange_CancelsOldRequest_AndOldFinallyKeepsReplacementInFlight()
    {
        using var env = new Round2Environment();
        var a = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        using var coordinator = env.Create(new Round2Adapter((settings, token) =>
        {
            if (settings.CliPath == "A") { oldToken = token; aStarted.SetResult(); return a.Task; }
            bStarted.SetResult(); return b.Task;
        }), env.Enabled(path: "A"));
        try
        {
            Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
            await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.ApplySettings(env.Enabled(path: "B"));
            Assert.True(oldToken.IsCancellationRequested);
            Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
            a.SetResult(env.Good("obsolete", 2));
            await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(coordinator.GetDisplayState(ProviderId.Codex).LastGood);
            env.Clock.Advance(TimeSpan.FromSeconds(20));
            Assert.True(coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
            Assert.False(coordinator.RequestManualRefresh(ProviderId.Codex));
            b.SetResult(env.Good("current", 70));
            await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
            Assert.Equal("current", coordinator.GetDisplayState(ProviderId.Codex).LastGood?.IdentityKey);
            Assert.Null(env.Cache.Load(ProviderId.Codex, "obsolete"));
            Assert.NotNull(env.Cache.Load(ProviderId.Codex, "current"));
        }
        finally { a.TrySetResult(env.Good()); b.TrySetResult(env.Good()); }
    }

    [Fact]
    public async Task ManualOnly_NoAutomaticQuery_OverrideAndCadenceChangesReschedule()
    {
        using var env = new Round2Environment();
        var count = 0;
        using var coordinator = env.Create(new Round2Adapter((_, _) => { Interlocked.Increment(ref count); return Task.FromResult(env.Good()); }));
        coordinator.ScanAndDispatch();
        await Task.Delay(80); Assert.Equal(0, count);
        Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
        await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        Assert.Equal(1, count);
        Assert.False(coordinator.RequestManualRefresh(ProviderId.Codex));
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        coordinator.ApplySettings(env.Enabled(30));
        coordinator.ScanAndDispatch(); await Task.Delay(60); Assert.Equal(1, count);
        coordinator.ApplySettings(env.Enabled(30) with { Providers = [new ProviderSettings { Id = ProviderId.Codex, Enabled = true, RefreshIntervalMinutes = 1 }] });
        await Round2Environment.Until(() => count == 2 && !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        coordinator.ApplySettings(env.Enabled());
        env.Clock.Advance(TimeSpan.FromHours(10)); coordinator.ScanAndDispatch();
        await Task.Delay(60); Assert.Equal(2, count);
    }

    [Fact]
    public void SchemaMigration_AndRoundTrip_PreserveLegacyChoicesAndCadence()
    {
        using var env = new Round2Environment();
        File.WriteAllText(env.Settings.FilePath, """{"schemaVersion":1,"leftClickOpensPopover":false,"remindersEnabled":true,"providers":[{"id":"Codex","enabled":true,"cliPath":"private-path"}]}""");
        var migrated = env.Settings.Load();
        Assert.Equal(3, migrated.SchemaVersion); Assert.Equal(5, migrated.RefreshIntervalMinutes);
        Assert.False(migrated.LeftClickOpensPopover); Assert.True(migrated.RemindersEnabled);
        Assert.Equal("private-path", migrated.Provider(ProviderId.Codex).CliPath);
        Assert.Null(migrated.Provider(ProviderId.Codex).RefreshIntervalMinutes);
        Assert.True(env.Settings.Save(migrated with { RefreshIntervalMinutes = 30,
            Providers = [migrated.Provider(ProviderId.Codex) with { RefreshIntervalMinutes = 0 }] }));
        var loaded = env.Settings.Load();
        Assert.Equal(TimeSpan.Zero, loaded.EffectiveRefreshInterval(ProviderId.Codex));
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.EffectiveRefreshInterval(ProviderId.Ark));
    }

    [Fact]
    public void CadenceChange_DoesNotRemoveBackoffOrAuthenticationPause()
    {
        using var env = new Round2Environment();
        var state = new ProviderRefreshState();
        var failure = env.Good() with { Error = ProviderErrorKind.Timeout, SucceededAtUtc = null };
        state.RecordAttempt(failure, env.Clock.UtcNow, TimeSpan.FromMinutes(30));
        state.Reschedule(TimeSpan.FromMinutes(1));
        Assert.Equal(env.Clock.UtcNow.AddMinutes(5), state.NextAutoAttemptUtc);
        state.Reschedule(TimeSpan.Zero); Assert.Null(state.NextAutoAttemptUtc);
        state.Reschedule(TimeSpan.FromMinutes(1)); Assert.Equal(env.Clock.UtcNow.AddMinutes(5), state.NextAutoAttemptUtc);
        state.RecordAttempt(failure with { Error = ProviderErrorKind.NotSignedIn }, env.Clock.UtcNow, TimeSpan.FromMinutes(5));
        state.Reschedule(TimeSpan.FromMinutes(1)); Assert.True(state.PausedUntilUserRetry); Assert.Null(state.NextAutoAttemptUtc);
    }

    [Fact]
    public async Task FreshnessScalesWithCadence_DiagnosticsNeverExportAccountOrPath()
    {
        using var env = new Round2Environment();
        using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good("private-account"))), env.Enabled(30, "private-path"));
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        env.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.False(coordinator.GetDisplayState(ProviderId.Codex).Stale);
        Assert.DoesNotContain("private-", coordinator.ExportDiagnostics());
        env.Clock.Advance(TimeSpan.FromMinutes(41));
        Assert.True(coordinator.GetDisplayState(ProviderId.Codex).Stale);
    }

    [Fact]
    public async Task ConcurrentProviders_NeverRunMoreThanTwoAdapters()
    {
        using var env = new Round2Environment();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0; var active = 0; var peak = 0;
        var adapters = Enum.GetValues<ProviderId>().ToDictionary(id => id, id => (IProviderAdapter)new Round2Adapter(async (_, _) =>
        {
            var concurrent = Interlocked.Increment(ref active);
            Interlocked.Increment(ref started);
            InterlockedExtensionsMax(ref peak, concurrent);
            try { await gate.Task; return env.Good() with { Provider = id }; }
            finally { Interlocked.Decrement(ref active); }
        }));
        env.Settings.Save(new MonitoringSettings { Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings { Id = id, Enabled = true }).ToList() });
        using var coordinator = new MonitoringCoordinator(env.Clock, new Round2Memory(), adapters, env.Settings, env.Cache);
        try
        {
            coordinator.RequestManualRefreshAll();
            await Round2Environment.Until(() => started >= 2);
            await Task.Delay(50);
            Assert.Equal(2, started); Assert.Equal(2, active);
        }
        finally { gate.TrySetResult(); }
        await Round2Environment.Until(() => started == 3 && coordinator.GetDisplayStates().All(state => !state.Refreshing));
        Assert.Equal(2, peak);
    }

    private static void InterlockedExtensionsMax(ref int target, int value)
    {
        int previous;
        do { previous = Volatile.Read(ref target); if (previous >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }

    [Fact]
    public async Task CacheClear_OnlyRemovesOwnedQuotaFiles_AndReportsDiskFailure()
    {
        using var env = new Round2Environment();
        using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())));
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        var originalSettings = File.ReadAllText(env.Settings.FilePath);
        var unrelated = Path.Combine(env.Cache.DirectoryPath, "unrelated.json");
        File.WriteAllText(unrelated, "do not remove");
        Assert.True(await coordinator.ClearQuotaCacheAsync());
        Assert.Null(coordinator.GetDisplayState(ProviderId.Codex).LastGood);
        Assert.Null(env.Cache.Load(ProviderId.Codex, "account"));
        Assert.Equal(originalSettings, File.ReadAllText(env.Settings.FilePath));
        Assert.Equal("do not remove", File.ReadAllText(unrelated));
        using var denied = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())), cache: new DeniedCache());
        Assert.False(await denied.ClearQuotaCacheAsync());
    }

    private sealed class DeniedCache : IMonitoringCache
    {
        public CachedProviderState? Load(ProviderId provider, string identityKey) => null;
        public bool Save(CachedProviderState state) => false;
        public void ClearProvider(ProviderId provider) => throw new UnauthorizedAccessException();
    }

    [Fact]
    public async Task QueuedReminder_CannotDeliverAfterDisableAndReenableOfSameAccount()
    {
        using var env = new Round2Environment();
        var settings = env.Enabled() with { RemindersEnabled = true };
        using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good(remaining: 2))), settings);
        QuotaReminderEvent? queued = null;
        coordinator.ReminderFired += value => queued ??= value;
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        Assert.NotNull(queued); Assert.True(coordinator.CanDeliverReminder(queued));
        coordinator.ApplySettings(new MonitoringSettings());
        Assert.False(coordinator.CanDeliverReminder(queued));
        coordinator.ApplySettings(settings);
        coordinator.RequestManualRefresh(ProviderId.Codex);
        await Round2Environment.Until(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        Assert.Equal("account", coordinator.GetDisplayState(ProviderId.Codex).LastGood?.IdentityKey);
        Assert.False(coordinator.CanDeliverReminder(queued));
    }

    [Fact]
    public void ProductVisibility_PreservesUnknownBuckets()
    {
        var settings = new MonitoringSettings { ShowArkAgentPlan = false, ShowArkCodingPlan = false };
        Assert.False(settings.IsProductVisible(ProviderId.Ark, "agent-plan"));
        Assert.False(settings.IsProductVisible(ProviderId.Ark, "coding-plan-team"));
        Assert.True(settings.IsProductVisible(ProviderId.Ark, "future-product"));
        Assert.True(settings.IsProductVisible(ProviderId.Codex, "agent-plan"));
    }
}
