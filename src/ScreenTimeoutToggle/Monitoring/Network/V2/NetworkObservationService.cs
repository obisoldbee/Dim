using OBDim.Monitoring.Network.Windows;

namespace OBDim.Monitoring.Network.V2;

/// <summary>
/// Interface-only v2 source. One owner per app, bounded source history, monotonic
/// byte-delta windows and generation fencing across stop/start. No TCP/PID claims.
/// </summary>
public sealed class NetworkObservationService : INetworkObservationSource, IDisposable
{
    public const int MaxInterfaces = 128;
    public const int MaxHistoryPoints = 28800;
    public const int MaxPointsPerInterface = 7201;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    private readonly IInterfaceCounterTable _reader;
    private readonly TimeProvider _time;
    private readonly Func<string?> _route;
    private readonly bool _schedule;
    private readonly object _gate = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;
    private int _busy;
    private long _generation, _version, _readStarted;
    public static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(5);
    public double LastCurrentLockWaitMs { get; private set; }
    public double LastReadMs { get; private set; }
    public double LastPublishMs { get; private set; }
    private bool _running, _disposed;
    private ObservationSnapshot _current = ObservationSnapshot.Empty;
    private string _session = "";
    private sealed class Track
    {
        public CounterInput? Last;
        public RateSample? LastSample;
        public CounterSettlement Settlement = new();
        public readonly List<RateSample> History = [];
        public long Epoch;
        public bool Missing, Trimmed;
        public string? PendingGap;
    }
    public NetworkObservationService(IInterfaceCounterTable? reader = null, TimeProvider? time = null,
        Func<string?>? route = null, bool schedule = true)
    {
        _reader = reader ?? new WindowsInterfaceCounterReader();
        _time = time ?? TimeProvider.System;
        _route = route ?? WindowsRouteSelection.Resolve;
        _schedule = schedule;
    }
    public event Action? Changed;
    public ObservationSnapshot Current
    {
        get
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_gate)
            {
                LastCurrentLockWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                return _current;
            }
        }
    }
    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool Busy => Volatile.Read(ref _busy) != 0;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || enabled == _running) return;
            _generation++;
            _running = enabled;
            _timer?.Dispose(); _timer = null;
            if (enabled)
            {
                _session = Guid.NewGuid().ToString("N");
                _tracks.Clear();
                _current = new(++_version, _session, _time.GetUtcNow(), Busy ? "waiting" : "starting", Busy ? "old-read-pending" : null, null, []);
                if (_schedule) _timer = _time.CreateTimer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
            else
                _current = _current with { Version = ++_version, PublishedAt = _time.GetUtcNow(), State = "stopped", Reason = "user-paused" };
        }
        Changed?.Invoke();
    }
    public void Refresh() { if (IsRunning) ThreadPool.QueueUserWorkItem(_ => Poll()); }

    internal void Poll()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { CheckReadDeadline(); return; }
        var notify = false;
        try
        {
            long generation;
            lock (_gate)
            {
                if (!_running || _disposed) return;
                generation = _generation; _readStarted = _time.GetTimestamp();
            }
            var readStart = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyList<InterfaceCounterRow> rows;
            NetworkReadStatus status;
            string? error, route = null;
            try
            {
                status = _reader.TryRead(out rows, out error);
                if (status == NetworkReadStatus.Ok) route = _route();
            }
            catch (Exception ex)
            {
                rows = []; status = NetworkReadStatus.Failed; error = ex.GetType().Name;
            }
            LastReadMs = System.Diagnostics.Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
            var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = _time.GetUtcNow();
            var mono = (ulong)((decimal)_time.GetTimestamp() * 1_000_000_000m / _time.TimestampFrequency);
            lock (_gate)
            {
                if (!_running || _disposed || generation != _generation) return;
                if (status != NetworkReadStatus.Ok)
                {
                    foreach (var track in _tracks.Values) track.PendingGap = "source-gap";
                    _current = _current with { Version = ++_version, PublishedAt = now,
                        State = status == NetworkReadStatus.AccessDenied ? "denied" : "disconnected",
                        Reason = status == NetworkReadStatus.AccessDenied ? "permission-denied" : "source-unavailable" };
                }
                else
                {
                    var bounded = rows.OrderByDescending(r => string.Equals(r.Id, route, StringComparison.OrdinalIgnoreCase))
                        .Take(MaxInterfaces).ToArray();
                    var present = bounded.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var (id, track) in _tracks)
                        if (!present.Contains(id)) { track.Missing = true; track.PendingGap = "interface-switch"; }
                    // Removed interfaces are not retained indefinitely. Selection belongs to
                    // the view and remains explicit even when its observation disappears.
                    foreach (var id in _tracks.Keys.Where(id => !present.Contains(id)).ToArray())
                        _tracks.Remove(id);
                    var perInterface = Math.Min(MaxPointsPerInterface, Math.Max(2, MaxHistoryPoints / Math.Max(1, bounded.Length)));
                    var observations = new List<InterfaceReading>();
                    foreach (var row in bounded)
                    {
                        if (!_tracks.TryGetValue(row.Id, out var track))
                            _tracks[row.Id] = track = new Track();
                        observations.Add(Observe(track, row, now, mono, perInterface));
                    }
                    var actualRoute = observations.FirstOrDefault(i => i.Available && string.Equals(i.Id, route, StringComparison.OrdinalIgnoreCase))?.Id;
                    _current = new(++_version, _session, now, "active",
                        rows.Count > MaxInterfaces ? "interface-limit" : null, actualRoute, observations)
                    { SourceInterfaceCount = rows.Count };
                }
                notify = true;
            }
            LastPublishMs = System.Diagnostics.Stopwatch.GetElapsedTime(publishStart).TotalMilliseconds;
        }
        finally { Volatile.Write(ref _busy, 0); }
        if (notify) Changed?.Invoke();
    }
    // A timer checks the one synchronous worker; no replacement worker is spawned.
    // Stop/dispose fence its result, but cannot cancel an OS API that never returns.
    private void CheckReadDeadline()
    {
        var notify = false;
        lock (_gate)
        {
            if (_disposed || !_running || !Busy || _current.State == "stalled") return;
            if (_time.GetElapsedTime(_readStarted) < ReadDeadline) return;
            _current = _current with { Version = ++_version, PublishedAt = _time.GetUtcNow(),
                State = "stalled", Reason = "read-timeout" };
            foreach (var track in _tracks.Values) track.PendingGap = "source-gap";
            notify = true;
        }
        if (notify) Changed?.Invoke();
    }
    private InterfaceReading Observe(Track t, InterfaceCounterRow row, DateTimeOffset now, ulong mono, int capacity)
    {
        var p = t.Last;
        if (t.Missing) { t.Epoch++; t.Missing = false; }
        var elapsed = p is null || mono <= p.MonotonicNs ? 0 : (mono - p.MonotonicNs) / 1e6;
        var globalGap = t.PendingGap;
        if (p is not null && now <= p.SampledAt) globalGap = "wall-clock-change";
        else if (p is not null && elapsed > 2500) globalGap = "silence";
        if (!row.IsUp) { globalGap = "interface-unavailable"; t.Missing = true; }
        var counters = new Directions<ulong?>(row.IsUp ? row.TxBytes : null, row.IsUp ? row.RxBytes : null);
        string? Reason(bool upload)
        {
            if (p is null) return "start";
            if (p.CounterEpoch != t.Epoch.ToString()) return "epoch-switch";
            if (globalGap is not null) return globalGap;
            if (counters[upload] is null || p.Bytes[upload] is null) return "counter-unavailable";
            if (counters[upload] < p.Bytes[upload]) return "counter-reset-or-wrap";
            return elapsed <= 0 ? "non-monotonic" : null;
        }
        var reasons = new Directions<string?>(Reason(true), Reason(false));
        double? Rate(bool upload) => reasons[upload] is null
            ? (counters[upload]!.Value - p!.Bytes[upload]!.Value) / (elapsed / 1000) : null;
        var input = new CounterInput("windows.ip-interface", row.Id, _session, t.Epoch.ToString(), now, mono, counters,
            new(globalGap, globalGap));
        var totals = t.Settlement.Apply(input);
        DirectionContinuity Edge(bool upload) => new(reasons[upload] is null ? "continuous" : "break",
            t.LastSample?.SampleID, reasons[upload]);
        var sample = new RateSample($"{_session}:{row.Id}:{mono}", input.SourceID, row.Id, _session,
            input.CounterEpoch, now, now, mono, elapsed > 0 ? elapsed : 1000,
            new(1000, 1000, "source-1s", "source-sampling"), new(Edge(true), Edge(false)), new(Rate(true), Rate(false)));
        if (p is null || mono > p.MonotonicNs)
        {
            t.History.Add(sample); t.Last = input; t.LastSample = sample;
        }
        t.PendingGap = null;
        // Retain by monotonic age too: wall-clock changes cannot keep old data forever.
        var removed = t.History.RemoveAll(s => mono > s.MonotonicNs && mono - s.MonotonicNs > 7_200_000_000_000UL);
        if (t.History.Count > capacity)
        {
            t.History.RemoveRange(0, t.History.Count - capacity);
            t.Trimmed = true;
        }
        return new(row.Id, row.Name, row.Kind.ToString(), now, sample.Rates, totals, counters,
            t.History.ToArray(), t.Settlement.ResetCount, t.Trimmed, row.IsUp);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _running = false; _generation++;
            _timer?.Dispose(); _timer = null;
        }
    }
}
