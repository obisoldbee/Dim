using System.Reflection;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.V2;
using OBDim.Monitoring.Network.Windows;

/// <summary>Deterministic counters feed the real bounded collector, only in the acceptance tool.</summary>
internal sealed class StressSource : INetworkObservationSource, IDisposable
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public long Tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Tick * 1000;
        public override DateTimeOffset GetUtcNow() => start.AddSeconds(Tick);
    }
    private sealed class Reader(Clock clock, string scenario) : IInterfaceCounterTable
    {
        public NetworkReadStatus TryRead(out IReadOnlyList<InterfaceCounterRow> rows, out string? error)
        {
            rows = Enumerable.Range(0, scenario == "interfaces" ? 129 : 1).Select(i =>
                new InterfaceCounterRow("probe-nic-" + i, "DEMO fixture " + i, InterfaceKind.Physical,
                    (ulong)clock.Tick * 10000, (ulong)clock.Tick * 100)).ToArray();
            error = null;
            return scenario == "gaps" && clock.Tick % 30 == 0 ? NetworkReadStatus.Failed : NetworkReadStatus.Ok;
        }
    }
    private readonly object _gate = new();
    private readonly Clock _clock;
    private readonly Action _poll;
    private readonly System.Threading.Timer _timer;
    private bool _paused;
    public NetworkObservationService Service { get; }
    public StressSource(string scenario)
    {
        var points = scenario == "interfaces" ? 225 : 7201;
        _clock = new Clock(DateTimeOffset.UtcNow.AddSeconds(-points));
        Service = new(new Reader(_clock, scenario), _clock, () => "probe-nic-0", false);
        _poll = typeof(NetworkObservationService).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(Service);
        Service.SetEnabled(true);
        for (var i = 0; i < points; i++) { _clock.Tick++; _poll(); }
        _timer = new(_ => Advance(), null, Timeout.Infinite, Timeout.Infinite);
    }
    private void Advance()
    {
        lock (_gate)
        {
            if (_paused || !Service.IsRunning) return;
            _clock.Tick++; _poll();
        }
    }
    public ObservationSnapshot Current => Service.Current;
    public bool IsRunning => Service.IsRunning;
    public event Action? Changed { add => Service.Changed += value; remove => Service.Changed -= value; }
    public void SetEnabled(bool value)
    {
        Service.SetEnabled(value);
        _timer.Change(value ? 1000 : Timeout.Infinite, value ? 1000 : Timeout.Infinite);
    }
    public void Refresh() => Advance();
    public void PauseUpdates() { lock (_gate) _paused = true; _timer.Change(Timeout.Infinite, Timeout.Infinite); }
    public void Dispose() { PauseUpdates(); _timer.Dispose(); Service.Dispose(); }
}
