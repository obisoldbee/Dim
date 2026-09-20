using OBDim.Monitoring.Network;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>
/// Service lifecycle and the coverage state machine (contract §4): stopped by default,
/// starting → active/partial, denied on OS refusal, last-good retention on failure.
/// The timer is parked at a huge interval; tests drive CollectOnce synchronously.
/// </summary>
public class NetworkMonitorServiceCoverageTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly FakeInterfaceCounterTable _interfaces = new();
    private readonly FakeConnectionTable _connections = new();
    private readonly FakeIdentityProvider _identities = new();
    private readonly List<NetworkSnapshot> _published = [];
    private NetworkMonitorService? _service;

    private NetworkMonitorService Service()
    {
        if (_service is not null) return _service;
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 1000, tx: 2000));
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.10", 443));
        var collector = NetFakes.Collector(_clock, _interfaces, _connections, _identities);
        _service = new NetworkMonitorService(_clock, collector, TimeSpan.FromHours(1));
        _service.SnapshotUpdated += s => _published.Add(s);
        return _service;
    }

    public void Dispose() => _service?.Dispose();

    [Fact]
    public void Default_IsStopped_WithEmptyPayload_AndHonestCapabilities()
    {
        var current = Service().Current;
        Assert.Equal(NetworkCoverage.Stopped, current.Coverage);
        Assert.Equal([CoverageReason.UserDisabled], current.CoverageReasons);
        Assert.Empty(current.Apps); // S5: non-data states carry no payload
        Assert.Empty(current.Interfaces);
        Assert.Null(current.DroppedEvents);
        Assert.Equal(SnapshotOrigin.Host, current.Origin);
        Assert.True(current.Capabilities.Observe);
        Assert.False(current.Capabilities.Block);     // read-only phase: always false
        Assert.False(current.Capabilities.Terminate); // read-only phase: always false
        Assert.False(Service().IsRunning);
    }

    [Fact]
    public void Start_PublishesStarting_ThenActiveOnFirstCollect()
    {
        var service = Service();
        service.Start();
        Assert.True(service.IsRunning);

        var starting = service.Current;
        Assert.Equal(NetworkCoverage.Starting, starting.Coverage);
        Assert.Equal([CoverageReason.CaptureInitializing], starting.CoverageReasons);
        Assert.Empty(starting.Apps);

        service.CollectOnce();
        var active = service.Current;
        Assert.Equal(NetworkCoverage.Active, active.Coverage);
        Assert.Empty(active.CoverageReasons); // S6
        Assert.Single(active.Apps);
        Assert.Single(active.Interfaces);
        Assert.Equal(0, active.DroppedEvents);
        Assert.Equal(2, _published.Count); // starting + active
        Assert.True(active.Sequence > starting.Sequence);
        Assert.True(active.CaptureStartedAt <= active.AsOf); // S4
    }

    [Fact]
    public void InterfaceSourceFailure_Partial_WithInterfaceCountersReason()
    {
        var service = Service();
        service.Start();
        _interfaces.Status = NetworkReadStatus.Failed;
        _interfaces.Error = "simulated";
        service.CollectOnce();

        var current = service.Current;
        Assert.Equal(NetworkCoverage.Partial, current.Coverage);
        Assert.Equal([CoverageReason.InterfaceCountersUnavailable], current.CoverageReasons);
        Assert.Empty(current.Interfaces);   // unknown scope, never zeroed counters
        Assert.Single(current.Apps);        // the healthy scope stays real
    }

    [Fact]
    public void ConnectionTableFailure_Partial_NullCounts_NeverZero()
    {
        var service = Service();
        service.Start();
        service.CollectOnce(); // one good sample so the app set is known
        Assert.Equal(NetworkCoverage.Active, service.Current.Coverage);

        _connections.TcpStatus = NetworkReadStatus.Failed;
        _clock.Advance(TimeSpan.FromSeconds(1));
        service.CollectOnce();

        var current = service.Current;
        Assert.Equal(NetworkCoverage.Partial, current.Coverage);
        Assert.Contains(CoverageReason.PidTablePartial, current.CoverageReasons);
        var app = Assert.Single(current.Apps);
        Assert.Null(app.ConnectionCount); // table unreadable: unknown, never reported as 0
        Assert.Empty(app.Connections);
        Assert.Single(service.Current.Interfaces); // interface scope unaffected
    }

    [Fact]
    public void AccessDenied_Denied_WithEmptyPayload()
    {
        var service = Service();
        service.Start();
        _connections.TcpStatus = NetworkReadStatus.AccessDenied;
        service.CollectOnce();

        var current = service.Current;
        Assert.Equal(NetworkCoverage.Denied, current.Coverage);
        Assert.Equal([CoverageReason.PermissionDenied], current.CoverageReasons);
        Assert.Empty(current.Apps);
        Assert.Empty(current.Interfaces);
        Assert.False(current.Capabilities.Observe);
        Assert.True(current.Capabilities.Permissions);
        Assert.NotNull(service.LastError);
    }

    [Fact]
    public void UnexpectedFailure_KeepsLastGoodSnapshot_AndItsTimestamps()
    {
        var service = Service();
        service.Start();
        service.CollectOnce();
        var good = service.Current;
        Assert.Equal(NetworkCoverage.Active, good.Coverage);

        _connections.ThrowOnRead = true;
        _clock.Advance(TimeSpan.FromSeconds(1));
        service.CollectOnce();

        Assert.Same(good, service.Current); // failure never rewrites the published snapshot
        Assert.Equal(good, service.LastGoodDataSnapshot);
        Assert.NotNull(service.LastError);
        var publishedCount = _published.Count;
        service.CollectOnce();
        Assert.Equal(publishedCount, _published.Count); // repeated failures publish nothing
    }

    [Fact]
    public void UnexpectedFailure_BeforeFirstGood_SurfacesDisconnected()
    {
        var service = Service();
        service.Start();
        _connections.ThrowOnRead = true;
        service.CollectOnce();

        var current = service.Current;
        Assert.Equal(NetworkCoverage.Disconnected, current.Coverage);
        Assert.Equal([CoverageReason.HostUnreachable], current.CoverageReasons);
        Assert.Empty(current.Apps);
        Assert.False(current.Capabilities.Observe);
        Assert.False(current.Capabilities.Permissions);
    }

    [Fact]
    public void Stop_PublishesStopped_StartAndStop_AreIdempotent()
    {
        var service = Service();
        service.Start();
        service.Start(); // idempotent: no second starting snapshot
        Assert.Single(_published);
        service.CollectOnce();

        service.Stop();
        Assert.False(service.IsRunning);
        Assert.Equal(NetworkCoverage.Stopped, service.Current.Coverage);
        Assert.Equal([CoverageReason.UserDisabled], service.Current.CoverageReasons);

        var count = _published.Count;
        service.Stop(); // idempotent: publishes nothing
        Assert.Equal(count, _published.Count);
    }

    [Fact]
    public void Restart_NewSnapshotEpoch_SequenceRestartsAtZero()
    {
        var service = Service();
        service.Start();
        service.CollectOnce();
        var firstEpoch = service.Current.Epoch;
        var lastSeq = service.Current.Sequence;
        service.Stop();

        service.Start();
        var restarted = service.Current;
        Assert.Equal(firstEpoch + 1, restarted.Epoch);
        Assert.Equal(0, restarted.Sequence); // sequence restarts inside the new epoch
        Assert.True(lastSeq >= restarted.Sequence);
    }
}
