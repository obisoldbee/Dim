using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Windows;

namespace OBDim.Monitoring.Network;

/// <summary>
/// Owns the network capture lifecycle: default OFF (the user opts in), idempotent
/// Start/Stop, a ~1 Hz bounded snapshot timer, and the coverage state machine of
/// contract §4. Closing any window never stops capture — Start/Stop are the only
/// switches, driven by the host's settings.
/// <para>
/// Coverage transitions: stopped (default) → starting (Start, first sample pending) →
/// active/partial (data flowing) → denied (OS refused a source) / back to stopped
/// (Stop). An unexpected collect failure keeps the last published snapshot untouched
/// (<see cref="LastError"/> records the cause); if no data snapshot was ever published,
/// the service surfaces disconnected instead of leaving "starting" up forever.
/// </para>
/// <para>
/// origin is always host. There is NO fixture or mock fallback anywhere in this class:
/// without a working host source the snapshots report denied/partial/disconnected with
/// real reason codes (contract §13).
/// </para>
/// </summary>
public sealed class NetworkMonitorService : INetworkSnapshotSource, IDisposable
{
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(1);

    private readonly IClock _clock;
    private readonly WindowsNetworkCollector _collector;
    private readonly TimeSpan _interval;
    private readonly object _stateLock = new();

    private System.Threading.Timer? _timer;
    private int _collectInFlight;
    private volatile bool _disposed;

    private NetworkSnapshot _current;
    private NetworkSnapshot? _lastGoodDataSnapshot;
    private string? _lastError;
    private bool _isRunning;
    private bool _everStarted;
    private long _epoch;
    private long _sequence;
    private long _captureStartedAtMs;

    public event Action<NetworkSnapshot>? SnapshotUpdated;

    public NetworkMonitorService(
        IClock clock,
        WindowsNetworkCollector collector,
        TimeSpan? interval = null)
    {
        _clock = clock;
        _collector = collector;
        _interval = interval ?? DefaultSampleInterval;
        var nowMs = NowMs();
        _captureStartedAtMs = nowMs;
        // Default state: user-disabled. Apps/interfaces empty by rule S5.
        _current = NetworkSnapshot.Lifecycle(
            NetworkCoverage.Stopped, CoverageReason.UserDisabled, nowMs, nowMs, _epoch, _sequence++);
    }

    /// <summary>Production wiring: real Windows readers, no stubs, no fixtures.</summary>
    public static NetworkMonitorService CreateDefault(IClock? clock = null)
    {
        var effectiveClock = clock ?? SystemClock.Instance;
        return new NetworkMonitorService(
            effectiveClock,
            new WindowsNetworkCollector(
                effectiveClock,
                new WindowsInterfaceCounterReader(),
                new WindowsConnectionTableReader(),
                new ProcessIdentityResolver()));
    }

    public NetworkSnapshot Current { get { lock (_stateLock) return _current; } }

    /// <summary>The last published data snapshot (active/partial), kept across failures so the UI can retain it with its timestamp.</summary>
    public NetworkSnapshot? LastGoodDataSnapshot { get { lock (_stateLock) return _lastGoodDataSnapshot; } }

    /// <summary>Sanitized cause of the most recent collect failure; null when the last cycle succeeded.</summary>
    public string? LastError { get { lock (_stateLock) return _lastError; } }

    public bool IsRunning { get { lock (_stateLock) return _isRunning; } }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposed || _isRunning) return; // idempotent
            _isRunning = true;
            _lastError = null;
            if (_everStarted)
            {
                // A new capture session is a new snapshot epoch; sequence restarts at 0
                // inside every epoch (contract §3.2/§3.3, epoch-rollover fixture).
                _epoch++;
                _sequence = 0;
            }
            _everStarted = true;
            _captureStartedAtMs = NowMs();

            PublishLocked(NetworkSnapshot.Lifecycle(
                NetworkCoverage.Starting, CoverageReason.CaptureInitializing,
                _captureStartedAtMs, _captureStartedAtMs, _epoch, NextSequenceLocked()));

            _timer = new System.Threading.Timer(
                _ => CollectOnce(), null, _interval, _interval);
        }
    }

    public void Stop()
    {
        System.Threading.Timer? timer;
        lock (_stateLock)
        {
            if (!_isRunning) return; // idempotent
            _isRunning = false;
            timer = _timer;
            _timer = null;

            PublishLocked(NetworkSnapshot.Lifecycle(
                NetworkCoverage.Stopped, CoverageReason.UserDisabled,
                NowMs(), _captureStartedAtMs, _epoch, NextSequenceLocked()));
        }
        timer?.Dispose();
    }

    /// <summary>
    /// One timer tick: collect, stamp epoch/sequence, enforce the message-level bounds,
    /// publish. Single-flight: a slow cycle never overlaps the next one.
    /// </summary>
    internal void CollectOnce()
    {
        if (Interlocked.CompareExchange(ref _collectInFlight, 1, 0) != 0) return;
        try
        {
            if (_disposed || !IsRunning) return;

            WindowsNetworkCollector.CollectResult result;
            try
            {
                result = _collector.Collect();
            }
            catch (Exception ex)
            {
                result = WindowsNetworkCollector.CollectResult.Failed($"unexpected collect failure: {ex.GetType().Name}");
            }

            lock (_stateLock)
            {
                if (!_isRunning || _disposed) return;

                switch (result.Status)
                {
                    case WindowsNetworkCollector.CollectStatus.Ok:
                    {
                        var payload = result.Payload!;
                        var coverage = payload.PartialReasons.Count == 0
                            ? NetworkCoverage.Active
                            : NetworkCoverage.Partial;
                        var snapshot = new NetworkSnapshot
                        {
                            SchemaVersion = NetworkSnapshot.ContractSchemaVersion,
                            Origin = SnapshotOrigin.Host,
                            AsOf = payload.SampleTimeMs,
                            CaptureStartedAt = _captureStartedAtMs,
                            Epoch = _epoch,
                            Sequence = NextSequenceLocked(),
                            Coverage = coverage,
                            CoverageReasons = payload.PartialReasons,
                            Capabilities = NetworkCapabilities.ReadOnly(observe: true, permissions: true),
                            DroppedEvents = payload.DroppedEvents,
                            Truncated = payload.Truncated,
                            Apps = payload.Apps,
                            Interfaces = payload.Interfaces,
                        };
                        snapshot = NetworkSnapshotBounder.Enforce(snapshot);
                        _lastError = null;
                        _lastGoodDataSnapshot = snapshot;
                        PublishLocked(snapshot);
                        break;
                    }
                    case WindowsNetworkCollector.CollectStatus.AccessDenied:
                        _lastError = result.Error;
                        PublishLocked(NetworkSnapshot.Lifecycle(
                            NetworkCoverage.Denied, CoverageReason.PermissionDenied,
                            NowMs(), _captureStartedAtMs, _epoch, NextSequenceLocked()));
                        break;
                    case WindowsNetworkCollector.CollectStatus.Failed:
                        _lastError = result.Error;
                        if (_lastGoodDataSnapshot is null)
                        {
                            // Nothing good to retain: surface disconnected rather than an
                            // eternal "starting" (contract §4 host-unreachable).
                            PublishLocked(NetworkSnapshot.Lifecycle(
                                NetworkCoverage.Disconnected, CoverageReason.HostUnreachable,
                                NowMs(), _captureStartedAtMs, _epoch, NextSequenceLocked()));
                        }
                        // Otherwise: the last published snapshot stays Current, with its
                        // original timestamps — failure never rewrites history.
                        break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _collectInFlight, 0);
        }
    }

    private long NextSequenceLocked() => _sequence++;

    private void PublishLocked(NetworkSnapshot snapshot)
    {
        _current = snapshot;
        SnapshotUpdated?.Invoke(snapshot);
    }

    private long NowMs() => NetworkSnapshot.ToUnixMs(_clock.UtcNow);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        System.Threading.Timer? timer;
        lock (_stateLock)
        {
            _isRunning = false;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }
}
