using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Services;

namespace OBDim.Monitoring.Services;

/// <summary>
/// The monitoring hub: owns the refresh scheduler, per-provider single-flight, the global
/// concurrency cap, cache round-trips, reminder evaluation and the memory sampler.
/// <para>
/// Design notes:
/// - Every UI-visible update raises <see cref="QuotaStateChanged"/> / <see cref="MemoryStateChanged"/>;
///   the panel (a WinForms control) marshals to its own thread.
/// - Refresh results are tagged with an identity generation so an in-flight response from
///   BEFORE an account switch or settings change can never overwrite newer state (plan §3.3).
/// - A failed refresh never touches the last good snapshot; it only records the attempt.
/// - All coordinator state is guarded by <see cref="_stateLock"/>: timer callbacks, the UI
///   thread and refresh continuations all touch it.
/// </para>
/// </summary>
public sealed class MonitoringCoordinator : IDisposable
{
    public static readonly TimeSpan MemorySampleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SchedulerTick = TimeSpan.FromSeconds(10);

    private readonly IClock _clock;
    private readonly IMemoryReader _memoryReader;
    private readonly IReadOnlyDictionary<ProviderId, IProviderAdapter> _adapters;
    private readonly MonitoringSettingsService _settingsService;
    private readonly MonitoringCacheService _cache;
    private readonly SemaphoreSlim _globalSlots = new(2, 2);
    private readonly object _stateLock = new();

    private readonly Dictionary<ProviderId, ProviderRefreshState> _refreshStates = [];
    private readonly Dictionary<ProviderId, string?> _identityKeys = [];
    private readonly Dictionary<ProviderId, int> _identityGenerations = [];
    private readonly Dictionary<ProviderId, ProviderSnapshot?> _lastAttempts = [];
    private readonly Dictionary<ProviderId, ProviderSnapshot?> _lastGoodSnapshots = [];
    private readonly Dictionary<ProviderId, HashSet<string>> _reminderMarks = [];

    private MonitoringSettings _settings;
    private readonly MemoryHistoryBuffer _memoryHistory;
    private MemorySample? _lastMemorySample;
    private string? _lastMemoryError;
    private DateTimeOffset? _lastMemoryAttemptUtc;

    private System.Threading.Timer? _memoryTimer;
    private System.Threading.Timer? _schedulerTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private volatile bool _disposed;

    public event Action? QuotaStateChanged;
    public event Action? MemoryStateChanged;
    public event Action<QuotaReminderEvent>? ReminderFired;

    public MonitoringCoordinator(
        IClock clock,
        IMemoryReader memoryReader,
        IReadOnlyDictionary<ProviderId, IProviderAdapter> adapters,
        MonitoringSettingsService settingsService,
        MonitoringCacheService cache,
        MemoryHistoryBuffer? memoryHistory = null)
    {
        _clock = clock;
        _memoryReader = memoryReader;
        _adapters = adapters;
        _settingsService = settingsService;
        _cache = cache;
        _memoryHistory = memoryHistory ?? new MemoryHistoryBuffer();
        _settings = _settingsService.Load();

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            _refreshStates[id] = new ProviderRefreshState();
            _identityKeys[id] = null;
            _identityGenerations[id] = 0;
            _lastAttempts[id] = null;
            _lastGoodSnapshots[id] = null;
            _reminderMarks[id] = [];
        }
    }

    public MonitoringSettings Settings => _settings;
    public MemoryHistoryBuffer MemoryHistory => _memoryHistory;

    public MemorySample? LatestMemorySample
    {
        get { lock (_stateLock) return _lastMemorySample; }
    }

    public string? LastMemoryError
    {
        get { lock (_stateLock) return _lastMemoryError; }
    }

    public DateTimeOffset? LastMemoryAttemptUtc
    {
        get { lock (_stateLock) return _lastMemoryAttemptUtc; }
    }

    public bool MemoryStale
    {
        get
        {
            lock (_stateLock)
            {
                return FreshnessEvaluator.IsStale(_lastMemorySample, _clock.UtcNow);
            }
        }
    }

    /// <summary>Loads settings and starts the memory sampler and the refresh scheduler.</summary>
    public void Start()
    {
        _memoryTimer = new System.Threading.Timer(
            _ => SampleMemory(),
            null,
            TimeSpan.Zero,
            MemorySampleInterval);

        // First scheduler tick comes quickly so an enabled provider queries right away
        // ("首次启用立即查询" — bounded by one tick, not by the 5-minute interval).
        _schedulerTimer = new System.Threading.Timer(
            _ => ScanAndDispatch(),
            null,
            TimeSpan.FromMilliseconds(500),
            SchedulerTick);
    }

    /// <summary>Timer tick: dispatch due auto-refreshes within the concurrency cap.</summary>
    internal void ScanAndDispatch()
    {
        if (_disposed) return;
        lock (_stateLock)
        {
            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                if (!IsAutoDispatchDueLocked(id)) continue;
                if (_globalSlots.CurrentCount == 0) return; // both slots busy — try next tick
                TryBeginDispatchLocked(id);
                _ = DispatchRefreshCoreAsync(id);
            }
        }
    }

    private bool IsAutoDispatchDueLocked(ProviderId id)
    {
        var state = _refreshStates[id];
        if (!_settings.Provider(id).Enabled) return false;
        if (state.InFlight) return false;
        if (state.PausedUntilUserRetry) return false;

        // Never attempted: due immediately (freshly enabled or first start).
        if (state.LastAttemptUtc is null) return true;
        return state.IsDueForAutoRefresh(_clock.UtcNow);
    }

    /// <summary>User-triggered refresh of one provider (manual rules, bypasses backoff and pause).</summary>
    public bool RequestManualRefresh(ProviderId id)
    {
        if (_disposed) return false;
        bool began;
        lock (_stateLock)
        {
            var state = _refreshStates[id];
            if (!_settings.Provider(id).Enabled) return false;
            if (!state.IsManualRefreshAllowed(_clock.UtcNow)) return false;

            state.RecordManualStart(_clock.UtcNow);
            state.ResetPause();
            began = TryBeginDispatchLocked(id);
        }
        if (began) _ = DispatchRefreshCoreAsync(id);
        return began;
    }

    /// <summary>User-triggered refresh of every enabled provider (the 全部刷新 button).</summary>
    public void RequestManualRefreshAll()
    {
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            RequestManualRefresh(id);
        }
    }

    /// <summary>Marks in-flight. Caller holds <see cref="_stateLock"/>; single-flight per provider is enforced here.</summary>
    private bool TryBeginDispatchLocked(ProviderId id)
    {
        var state = _refreshStates[id];
        if (state.InFlight) return false;
        state.InFlight = true;
        return true;
    }

    private async Task DispatchRefreshCoreAsync(ProviderId id)
    {
        int generation;
        ProviderSettings settings;
        DateTimeOffset attemptedAt;
        lock (_stateLock)
        {
            generation = _identityGenerations[id];
            settings = _settings.Provider(id);
            attemptedAt = _clock.UtcNow;
        }

        try
        {
            await _globalSlots.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock) _refreshStates[id].InFlight = false;
            return;
        }

        try
        {
            if (!_adapters.TryGetValue(id, out var adapter))
            {
                lock (_stateLock) _refreshStates[id].InFlight = false;
                return;
            }

            var snapshot = await adapter.QueryAsync(settings, _lifetimeCts.Token).ConfigureAwait(false);
            ApplySnapshot(id, snapshot, generation);
        }
        catch (OperationCanceledException)
        {
            var cancelled = SnapshotFactory.Failure(id, ProviderErrorKind.Cancelled, null, attemptedAt);
            ApplySnapshot(id, cancelled, generation);
        }
        catch (Exception ex)
        {
            // The adapter contract says it should not throw, but a coordinator crash would
            // take the whole tray app with it — record as a classified failure instead.
            LogService.Error($"Monitoring refresh failed unexpectedly for {id}", ex);
            var failure = SnapshotFactory.Failure(id, ProviderErrorKind.ExecutionFailed, "internal error", attemptedAt);
            ApplySnapshot(id, failure, generation);
        }
        finally
        {
            _globalSlots.Release();
            RaiseQuotaStateChanged();
        }
    }

    private void ApplySnapshot(ProviderId id, ProviderSnapshot snapshot, int generation)
    {
        if (_disposed) return;
        List<QuotaReminderEvent>? reminderEvents = null;
        var persistState = false;

        lock (_stateLock)
        {
            if (generation != _identityGenerations[id])
            {
                // The identity or settings changed while this query was in flight — the
                // result describes an account context that no longer applies. Discard.
                _refreshStates[id].InFlight = false;
                return;
            }

            var now = _clock.UtcNow;
            if (_identityKeys[id] != snapshot.IdentityKey)
            {
                _identityKeys[id] = snapshot.IdentityKey;
                _identityGenerations[id]++;
                // Account context switched: the old account's quota and reminder state must
                // not survive. Verified identities may load their matching cache; unverified
                // ones (MiniMax) never reuse cross-session state (spec §8(4)).
                _reminderMarks[id] = LoadMarks(id, snapshot.IdentityKey);
                if (!snapshot.HasError && snapshot.IdentityVerified)
                {
                    var cached = _cache.Load(id, snapshot.IdentityKey);
                    if (cached?.LastGood is not null)
                    {
                        // Cached values are shown immediately but keep their own (old)
                        // success time — the UI marks them stale until the next success.
                        _lastGoodSnapshots[id] = cached.LastGood;
                    }
                }
                else if (!snapshot.IdentityVerified)
                {
                    _lastGoodSnapshots[id] = null;
                }
            }

            _lastAttempts[id] = snapshot.HasError ? snapshot : null;
            _refreshStates[id].RecordAttempt(snapshot, now);

            if (!snapshot.HasError && snapshot.SucceededAtUtc is not null)
            {
                _lastGoodSnapshots[id] = snapshot;
                if (snapshot.IdentityVerified) persistState = true;
            }

            if (_settings.RemindersEnabled && !snapshot.HasError)
            {
                try
                {
                    reminderEvents = [.. ReminderEvaluator.Evaluate(snapshot, _reminderMarks[id], now)];
                    if (reminderEvents.Count > 0 && snapshot.IdentityVerified) persistState = true;
                }
                catch (Exception ex)
                {
                    // Reminder failure must never disturb the quota display (plan §3.3).
                    LogService.Error("Reminder evaluation failed", ex);
                }
            }
        }

        if (persistState)
        {
            PersistProviderState(id, snapshot);
        }

        if (reminderEvents is { Count: > 0 })
        {
            foreach (var evt in reminderEvents)
            {
                ReminderFired?.Invoke(evt);
            }
        }
    }

    private void PersistProviderState(ProviderId id, ProviderSnapshot snapshot)
    {
        HashSet<string> marks;
        lock (_stateLock)
        {
            marks = [.. _reminderMarks[id]];
        }
        try
        {
            _cache.Save(new CachedProviderState
            {
                Provider = id,
                IdentityKey = snapshot.IdentityKey,
                LastGood = snapshot,
                ReminderFiredMarks = marks.ToDictionary(k => k, _ => true),
            });
        }
        catch (Exception ex)
        {
            // Cache write failure is reported, never faked (spec §8(6)); display continues.
            LogService.Error("Monitoring cache save failed", ex);
        }
    }

    private HashSet<string> LoadMarks(ProviderId id, string identityKey)
    {
        var stored = _cache.Load(id, identityKey)?.ReminderFiredMarks;
        return stored is { Count: > 0 } ? [.. stored.Keys] : [];
    }

    /// <summary>Builds the immutable view state for the panel. Snapshot + refresh state + freshness, never a doctored snapshot.</summary>
    public ProviderDisplayState GetDisplayState(ProviderId id)
    {
        lock (_stateLock)
        {
            var settings = _settings.Provider(id);
            var state = _refreshStates[id];
            var lastGood = _lastGoodSnapshots[id];
            return new ProviderDisplayState
            {
                Provider = id,
                Enabled = settings.Enabled,
                Refreshing = state.InFlight,
                LastGood = lastGood,
                LastAttempt = _lastAttempts[id],
                Stale = lastGood is not null && FreshnessEvaluator.IsStale(lastGood, _clock.UtcNow),
                PausedUntilUserRetry = state.PausedUntilUserRetry,
            };
        }
    }

    public IReadOnlyList<ProviderDisplayState> GetDisplayStates() =>
        Enum.GetValues<ProviderId>().Select(GetDisplayState).ToList();

    private void SampleMemory()
    {
        if (_disposed) return;
        lock (_stateLock)
        {
            if (!_settings.MemoryEnabled)
            {
                // Disabled: no sampling, no growth (spec A06); the last state stays frozen.
                return;
            }
        }

        try
        {
            var sample = _memoryReader.Read(out var error);
            lock (_stateLock)
            {
                _lastMemoryAttemptUtc = _clock.UtcNow;
                _lastMemoryError = sample is null ? (error ?? "memory read failed") : null;
                if (sample is not null)
                {
                    _lastMemorySample = sample;
                    _memoryHistory.Add(sample);
                }
            }
            MemoryStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Sampling must never take the process down.
            LogService.Error("Memory sampling failed", ex);
        }
    }

    /// <summary>Applies new settings from the monitoring settings form.</summary>
    public void ApplySettings(MonitoringSettings newSettings)
    {
        bool changedEnables = false;
        lock (_stateLock)
        {
            var old = _settings;
            _settings = newSettings;

            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                var oldP = old.Provider(id);
                var newP = newSettings.Provider(id);
                var state = _refreshStates[id];

                if (!Equals(oldP.CliPath, newP.CliPath))
                {
                    // A path change invalidates both the version probe and the pause state.
                    state.ResetPause();
                    state.LastAttemptUtc = null;
                    changedEnables = true;
                }

                if (!oldP.Enabled && newP.Enabled)
                {
                    // First enable → query immediately (spec §7).
                    state.ResetPause();
                    state.LastAttemptUtc = null;
                    changedEnables = true;
                }

                if (oldP.Enabled && !newP.Enabled)
                {
                    state.ResetPause();
                }
            }
        }

        if (changedEnables)
        {
            ScanAndDispatch();
        }
        RaiseQuotaStateChanged();
    }

    public void RaiseQuotaStateChanged() => QuotaStateChanged?.Invoke();

    /// <summary>Persists settings; false means the write failed (caller reports, never fakes).</summary>
    public bool SaveSettings(MonitoringSettings settings) => _settingsService.Save(settings);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        _memoryTimer?.Dispose();
        _schedulerTimer?.Dispose();
        _memoryReader.Dispose();
        _lifetimeCts.Dispose();
        _globalSlots.Dispose();
    }
}
