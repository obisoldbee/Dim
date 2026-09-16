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
    private readonly IMonitoringCache _cache;
    private readonly SemaphoreSlim _globalSlots = new(2, 2);
    private readonly object _stateLock = new();

    private readonly Dictionary<ProviderId, ProviderRefreshState> _refreshStates = [];
    private readonly Dictionary<ProviderId, string?> _identityKeys = [];
    private readonly Dictionary<ProviderId, int> _identityGenerations = [];
    private readonly Dictionary<ProviderId, ProviderSnapshot?> _lastAttempts = [];
    private readonly Dictionary<ProviderId, ProviderSnapshot?> _lastGoodSnapshots = [];
    private readonly Dictionary<ProviderId, HashSet<string>> _reminderMarks = [];

    private sealed record RefreshRequest(ProviderId Id, int Generation, ProviderSettings Settings,
        DateTimeOffset AttemptedAt, CancellationTokenSource Cancellation);
    private readonly Dictionary<ProviderId, RefreshRequest?> _requests = [];
    private readonly Dictionary<ProviderId, SemaphoreSlim> _providerSlots = [];

    private MonitoringSettings _settings;
    private readonly MemoryHistoryBuffer _memoryHistory;
    private MemorySample? _lastMemorySample;
    private string? _lastMemoryError;
    private DateTimeOffset? _lastMemoryAttemptUtc;

    private System.Threading.Timer? _memoryTimer;
    private System.Threading.Timer? _schedulerTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private volatile bool _disposed;
    private bool _clearingCache;
    private readonly HashSet<ProviderId> _authenticating = [];

    public event Action? QuotaStateChanged;
    public event Action? MemoryStateChanged;
    public event Action<QuotaReminderEvent>? ReminderFired;

    /// <summary>Raised after ApplySettings so hosts can react (e.g. re-register the popover hotkey).</summary>
    public event Action? SettingsApplied;

    public MonitoringCoordinator(
        IClock clock,
        IMemoryReader memoryReader,
        IReadOnlyDictionary<ProviderId, IProviderAdapter> adapters,
        MonitoringSettingsService settingsService,
        IMonitoringCache cache,
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
            _requests[id] = null;
            _providerSlots[id] = new SemaphoreSlim(1, 1);
        }
    }

    public MonitoringSettings Settings { get { lock (_stateLock) return _settings; } }
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

    /// <summary>Reserve requests under the lock; the entire CLI/cache operation starts on a worker.</summary>
    internal void ScanAndDispatch()
    {
        List<RefreshRequest> due = [];
        lock (_stateLock)
        {
            if (_disposed || _clearingCache) return;
            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                var state = _refreshStates[id];
                if (_authenticating.Contains(id) || !_settings.Provider(id).Enabled || _settings.EffectiveRefreshInterval(id) == TimeSpan.Zero
                    || state.InFlight || state.PausedUntilUserRetry) continue;
                if (state.LastAttemptUtc is not null && !state.IsDueForAutoRefresh(_clock.UtcNow)) continue;
                if (BeginRequestLocked(id) is { } request) due.Add(request);
            }
        }
        foreach (var request in due) QueueRequest(request);
    }

    public bool RequestManualRefresh(ProviderId id)
    {
        RefreshRequest? request;
        lock (_stateLock)
        {
            if (_disposed || _clearingCache || _authenticating.Contains(id) || !_settings.Provider(id).Enabled) return false;
            var state = _refreshStates[id];
            if (!state.IsManualRefreshAllowed(_clock.UtcNow)) return false;
            request = BeginRequestLocked(id);
            if (request is null) return false;
            state.RecordManualStart(_clock.UtcNow);
            state.ResetPause();
        }
        QueueRequest(request);
        return true;
    }

    /// <summary>Explicit login invalidates old account work, without altering saved settings.</summary>
    public bool BeginAuthentication(ProviderId id)
    {
        CancellationTokenSource? cancel;
        lock (_stateLock)
        {
            if (_disposed || _clearingCache || !_settings.Provider(id).Enabled || !_authenticating.Add(id)) return false;
            _identityGenerations[id]++;
            cancel = _requests[id]?.Cancellation;
            _requests[id] = null;
            _refreshStates[id].InFlight = false;
            _refreshStates[id].PausedUntilUserRetry = true;
            _lastGoodSnapshots[id] = null; _lastAttempts[id] = null;
            _identityKeys[id] = null; _reminderMarks[id] = [];
        }
        try { if (cancel is not null) _ = cancel.CancelAsync(); } catch (ObjectDisposedException) { }
        RaiseQuotaStateChanged();
        return true;
    }

    public void EndAuthentication(ProviderId id, bool verify)
    {
        lock (_stateLock)
        {
            _authenticating.Remove(id);
            _refreshStates[id].LastManualStartUtc = null;
            if (verify) _refreshStates[id].ResetPause();
        }
        if (verify) RequestManualRefresh(id); // exit code alone is never authenticated evidence
        RaiseQuotaStateChanged();
    }

    public void RequestManualRefreshAll()
    {
        foreach (ProviderId id in Enum.GetValues<ProviderId>()) RequestManualRefresh(id);
    }

    private RefreshRequest? BeginRequestLocked(ProviderId id)
    {
        if (_refreshStates[id].InFlight) return null;
        var request = new RefreshRequest(id, _identityGenerations[id], _settings.Provider(id),
            _clock.UtcNow, CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token));
        _requests[id] = request;
        _refreshStates[id].InFlight = true;
        return request;
    }

    private void QueueRequest(RefreshRequest request) =>
        _ = Task.Run(() => DispatchRefreshCoreAsync(request));

    private bool IsCurrentLocked(RefreshRequest request) =>
        !_disposed && request.Generation == _identityGenerations[request.Id]
        && ReferenceEquals(_requests[request.Id], request) && _settings.Provider(request.Id).Enabled;

    private bool IsCurrent(RefreshRequest request)
    {
        lock (_stateLock) return IsCurrentLocked(request);
    }

    private async Task DispatchRefreshCoreAsync(RefreshRequest request)
    {
        var id = request.Id;
        var token = request.Cancellation.Token;
        var providerAcquired = false;
        var globalAcquired = false;
        try
        {
            // A changed context may queue a replacement, but the old process and cache
            // writer must finish first. Cancellation never creates a third CLI process.
            await _providerSlots[id].WaitAsync(token).ConfigureAwait(false);
            providerAcquired = true;
            await _globalSlots.WaitAsync(token).ConfigureAwait(false);
            globalAcquired = true;
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(request) || !_adapters.TryGetValue(id, out var adapter)) return;
            ProviderSnapshot snapshot;
            try
            {
                snapshot = await adapter.QueryAsync(request.Settings, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LogService.Error($"Monitoring refresh failed unexpectedly for {id}", ex);
                snapshot = SnapshotFactory.Failure(id, ProviderErrorKind.ExecutionFailed, "internal error", request.AttemptedAt);
            }
            if (!token.IsCancellationRequested) ApplySnapshot(request, snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error($"Monitoring worker failed for {id}", ex);
        }
        finally
        {
            if (globalAcquired) _globalSlots.Release();
            if (providerAcquired) _providerSlots[id].Release();
            lock (_stateLock)
            {
                // An obsolete finally must not release the replacement's single-flight.
                if (ReferenceEquals(_requests[id], request))
                {
                    _refreshStates[id].InFlight = false;
                    _requests[id] = null;
                }
            }
            request.Cancellation.Dispose();
            RaiseQuotaStateChanged();
        }
    }

    private void ApplySnapshot(RefreshRequest request, ProviderSnapshot snapshot)
    {
        var id = request.Id;
        bool identityChanged;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(request)) return;
            identityChanged = snapshot.IdentityVerified && _identityKeys[id] != snapshot.IdentityKey;
        }
        // Disk I/O is serialized per provider, never protected by the UI's state lock.
        var cached = identityChanged ? _cache.Load(id, snapshot.IdentityKey) : null;
        List<QuotaReminderEvent> reminders = [];
        CachedProviderState? persist = null;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(request)) return; // settings can change during cache read
            var now = _clock.UtcNow;
            if (identityChanged)
            {
                _identityKeys[id] = snapshot.IdentityKey;
                _reminderMarks[id] = cached is null ? [] : new HashSet<string>(cached.ReminderFiredMarks.Where(p => p.Value).Select(p => p.Key));
                _lastGoodSnapshots[id] = cached?.LastGood;
            }
            if (ProviderFailureClassifier.IsAuthenticationFailure(snapshot.Error))
            {
                _lastGoodSnapshots[id] = null;
                _reminderMarks[id] = [];
                _identityKeys[id] = null;
            }
            _lastAttempts[id] = snapshot.HasError ? snapshot : null;
            _refreshStates[id].RecordAttempt(snapshot, now, _settings.EffectiveRefreshInterval(id));
            _refreshStates[id].InFlight = true; // includes persistence, until this worker settles
            if (!snapshot.HasError && snapshot.SucceededAtUtc is not null)
            {
                if (snapshot.IsPartial && _lastGoodSnapshots[id] is { } previous
                    && previous.IdentityKey == snapshot.IdentityKey)
                    snapshot = SnapshotMerger.MergePartial(previous, snapshot);
                _lastGoodSnapshots[id] = snapshot;
                if (_settings.RemindersEnabled)
                    reminders = [.. ReminderEvaluator.Evaluate(snapshot, _reminderMarks[id], now,
                        FreshnessEvaluator.MaxAge(_settings.EffectiveRefreshInterval(id)))];
                if (snapshot.IdentityVerified)
                    persist = new CachedProviderState
                    {
                        Provider = id, IdentityKey = snapshot.IdentityKey, LastGood = snapshot,
                        ReminderFiredMarks = _reminderMarks[id].ToDictionary(key => key, _ => true),
                    };
            }
        }
        if (!IsCurrent(request)) return;
        if (ProviderFailureClassifier.IsAuthenticationFailure(snapshot.Error)) _cache.ClearProvider(id);
        else if (persist is not null)
        {
            _cache.Save(persist);
            // The per-provider slot still belongs to us, so an invalidated write can
            // be removed without ever deleting a newer request's cache.
            if (!IsCurrent(request)) { _cache.ClearProvider(id); return; }
        }
        var version = System.Text.RegularExpressions.Regex.Match(snapshot.CliVersion ?? "", @"\b\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?\b");
        var elapsedMs = Math.Max(0, (_clock.UtcNow - request.AttemptedAt).TotalMilliseconds);
        var detail = snapshot.HasError ? $"failed: {snapshot.Error}" : $"ok: {snapshot.Buckets.Count} bucket(s)";
        LogService.Info($"Monitoring[{id}] quota query {detail}; elapsed={elapsedMs:0}ms cli={(version.Success ? version.Value : "?")}");
        foreach (var reminder in reminders)
        {
            lock (_stateLock)
            {
                if (!IsCurrentLocked(request) || !_settings.RemindersEnabled) return;
            }
            ReminderFired?.Invoke(reminder with { ContextGeneration = request.Generation });
        }
    }

    internal bool CanDeliverReminder(QuotaReminderEvent reminder)
    {
        lock (_stateLock)
        {
            return !_disposed && _settings.RemindersEnabled && _settings.Provider(reminder.Provider).Enabled
                && reminder.ContextGeneration == _identityGenerations[reminder.Provider]
                && reminder.IdentityKey == _identityKeys[reminder.Provider];
        }
    }

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
                Authenticating = _authenticating.Contains(id),
                LastGood = lastGood,
                LastAttempt = _lastAttempts[id],
                Stale = lastGood is not null && FreshnessEvaluator.IsStale(lastGood, _clock.UtcNow, FreshnessEvaluator.MaxAge(_settings.EffectiveRefreshInterval(id))),
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
        List<CancellationTokenSource> cancelled = [];
        lock (_stateLock)
        {
            if (_disposed) return;
            var old = _settings;
            _settings = MonitoringSettingsService.Normalize(newSettings);
            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                var before = old.Provider(id);
                var after = _settings.Provider(id);
                var state = _refreshStates[id];
                if (before.Enabled != after.Enabled || !string.Equals(before.CliPath, after.CliPath, StringComparison.Ordinal))
                {
                    _identityGenerations[id]++;
                    if (_requests[id] is { } active) cancelled.Add(active.Cancellation);
                    _requests[id] = null;
                    state.InFlight = false;
                    state.ResetPause();
                    state.LastAttemptUtc = null;
                    state.LastManualStartUtc = null;
                    // A different executable is a different account context until verified.
                    _identityKeys[id] = null;
                    _lastGoodSnapshots[id] = null;
                    _lastAttempts[id] = null;
                    _reminderMarks[id] = [];
                }
                else if (old.EffectiveRefreshInterval(id) != _settings.EffectiveRefreshInterval(id))
                {
                    state.Reschedule(_settings.EffectiveRefreshInterval(id));
                }
            }
        }
        foreach (var cts in cancelled)
        {
            try { _ = cts.CancelAsync(); } catch (ObjectDisposedException) { }
        }
        ScanAndDispatch();
        RaiseQuotaStateChanged();
        SettingsApplied?.Invoke();
    }

    /// <summary>Only owned quota state files are cleared; credentials/settings/history are untouched.</summary>
    public async Task<bool> ClearQuotaCacheAsync()
    {
        List<CancellationTokenSource> cancelled = [];
        lock (_stateLock)
        {
            if (_disposed || _clearingCache) return false;
            _clearingCache = true;
            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                _identityGenerations[id]++;
                if (_requests[id] is { } active) cancelled.Add(active.Cancellation);
                _requests[id] = null;
                _refreshStates[id].InFlight = false;
                _lastGoodSnapshots[id] = null; _lastAttempts[id] = null;
                _identityKeys[id] = null; _reminderMarks[id] = [];
            }
        }
        foreach (var cts in cancelled)
            try { _ = cts.CancelAsync(); } catch (ObjectDisposedException) { }
        var success = await Task.Run(async () =>
        {
            var ok = true;
            foreach (ProviderId id in Enum.GetValues<ProviderId>())
            {
                await _providerSlots[id].WaitAsync().ConfigureAwait(false);
                try { _cache.ClearProvider(id); }
                catch (Exception) { ok = false; }
                finally { _providerSlots[id].Release(); }
            }
            return ok;
        }).ConfigureAwait(false);
        lock (_stateLock) _clearingCache = false;
        RaiseQuotaStateChanged();
        return success;
    }

    public string ExportDiagnostics() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Version = typeof(MonitoringCoordinator).Assembly.GetName().Version?.ToString(),
        Providers = GetDisplayStates().Select(state => new
        {
            Provider = state.Provider.ToString(), state.Enabled, state.Refreshing, state.Stale,
            state.PausedUntilUserRetry, state.Authenticating, Error = state.LastAttempt?.Error.ToString(),
            ErrorCode=state.LastAttempt?.ErrorCode, ExitCode=state.LastAttempt?.ExitCode,
            LastSuccessUtc = state.LastGood?.SucceededAtUtc, BucketCount = state.LastGood?.Buckets.Count ?? 0,
        }),
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

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
        // Workers own their linked CTS until finally. SemaphoreSlim has no native
        // wait handle here; retaining it until GC lets outstanding workers release safely.
    }
}
