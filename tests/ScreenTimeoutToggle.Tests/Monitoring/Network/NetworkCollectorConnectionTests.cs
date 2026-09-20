using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>Connection identity, direction pairing, end idempotence, system bucket, synthetic identities.</summary>
public class NetworkCollectorConnectionTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeInterfaceCounterTable _interfaces = new();
    private readonly FakeConnectionTable _connections = new();
    private readonly FakeIdentityProvider _identities = new();

    private WindowsNetworkCollector NewCollector() =>
        NetFakes.Collector(_clock, _interfaces, _connections, _identities);

    [Fact]
    public void OneTransportConnection_TwoDirectionalObservations_BytesUnknown()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.10", 443));
        var payload = NewCollector().Collect().Payload!;

        var app = Assert.Single(payload.Apps);
        Assert.Equal(2, app.Connections.Count);
        Assert.Equal(2, app.ConnectionCount); // count matches emitted observations, never faked
        var up = Assert.Single(app.Connections, c => c.Direction == ConnectionDirection.Up);
        var down = Assert.Single(app.Connections, c => c.Direction == ConnectionDirection.Down);
        Assert.NotEqual(up.Id, down.Id);
        Assert.Null(up.Bytes);  // no per-connection byte source in N2: unknown, never 0
        Assert.Null(down.Bytes);
        Assert.Equal("203.0.113.10", up.Ip);
        Assert.Equal(443, up.Port);
        Assert.Equal(ConnectionInitiator.Unknown, up.Initiator); // no initiator evidence
        Assert.Null(up.Hostname);
        Assert.Equal(DomainSource.Unknown, up.DomainSource); // hostname null ⇒ source unknown (S2)
        Assert.Equal("unknown", up.Route);
        Assert.Null(up.ProxyFlowId);
        Assert.Equal("ESTABLISHED", up.State);
        Assert.StartsWith(app.Id + "/tcp/192.168.1.10:50000/203.0.113.10:443/", up.Id);
    }

    [Fact]
    public void StableObservationId_WhileConnectionLives()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.10", 443));
        var collector = NewCollector();
        var first = collector.Collect().Payload!;
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = collector.Collect().Payload!;

        Assert.Equal(
            first.Apps[0].Connections.Select(c => c.Id),
            second.Apps[0].Connections.Select(c => c.Id));
    }

    [Fact]
    public void EndedConnection_SettlesOnce_ReappearingTuple_IsANewIdentity()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.10", 443));
        var collector = NewCollector();
        var first = collector.Collect().Payload!;
        var idsBefore = first.Apps[0].Connections.Select(c => c.Id).ToList();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _connections.TcpRows.Clear(); // connection ended
        var ended = collector.Collect().Payload!;
        Assert.Empty(ended.Apps); // nothing live: the app has no observable connections left

        _clock.Advance(TimeSpan.FromSeconds(1));
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.10", 443)); // same tuple, new connection
        var reopened = collector.Collect().Payload!;
        var idsAfter = Assert.Single(reopened.Apps).Connections.Select(c => c.Id).ToList();

        Assert.Equal(2, idsAfter.Count);
        foreach (var id in idsAfter)
            Assert.DoesNotContain(id, idsBefore); // settled ids are never reused (end settles once)
    }

    [Fact]
    public void KernelPids_GoToUnknownSystemBucket_WithExplanation()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 0, "203.0.113.10", 443));
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 4, "203.0.113.20", 445));
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.30", 80));
        var payload = NewCollector().Collect().Payload!;

        Assert.Equal(2, payload.Apps.Count);
        var bucket = Assert.Single(payload.Apps, a => a.Id == "unknown-system");
        Assert.Equal("Unknown (system process)", bucket.Name);
        Assert.Equal("fallback-name", bucket.IdentitySource);
        Assert.Equal(4, bucket.ConnectionCount); // 2 kernel connections × 2 directions
        Assert.Contains(bucket.Processes, p => p.Pid is 0 or 4);
        // The regular pid is attributed to its own app, never to the bucket.
        var app = Assert.Single(payload.Apps, a => a.Id != "unknown-system");
        Assert.Single(app.Processes);
        Assert.Equal(100, app.Processes[0].Pid);
    }

    [Fact]
    public void UnresolvableIdentity_SyntheticUnknownExe_StableAcrossCycles()
    {
        _identities.ByPid[200] = null; // protected/gone: every resolution tier fails
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 200, "203.0.113.44", 8080));
        var collector = NewCollector();

        var first = collector.Collect().Payload!;
        var app = Assert.Single(first.Apps);
        Assert.Equal("fallback-name", app.IdentitySource);
        var proc = Assert.Single(app.Processes);
        Assert.Equal("unknown.exe (PID 200)", proc.Name);
        Assert.True(proc.StartTime > 0, "synthetic startTime is the first-seen lower bound, never 0");
        Assert.Null(proc.ExecutablePath);
        Assert.Null(app.Path);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = collector.Collect().Payload!;
        var appAgain = Assert.Single(second.Apps);
        Assert.Equal(0, appAgain.Epoch); // stable synthetic identity: no fake PID-reuse per cycle
        Assert.Equal(proc.StartTime, Assert.Single(appAgain.Processes).StartTime);
    }

    [Fact]
    public void ParallelConnections_ToSameRemote_HaveDistinctIds()
    {
        // The real-machine failure mode: one app holding many connections to the same
        // host:port (different local ports) — ids must stay unique within the app (S1).
        for (var i = 0; i < 5; i++)
            _connections.TcpRows.Add(NetFakes.Tcp(100, "203.0.113.10", 443, localPort: 50000 + i));
        var payload = NewCollector().Collect().Payload!;

        var app = Assert.Single(payload.Apps);
        Assert.Equal(10, app.Connections.Count);
        Assert.Equal(10, app.Connections.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void ListenAndEndpointLessRows_NeverBecomeConnections()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "0.0.0.0", 0, state: "LISTEN"));
        _connections.TcpRows.Add(new ConnectionRow(
            NetworkProtocol.Tcp, "192.168.1.10", 50001, null, null, "BOUND", 100));
        var payload = NewCollector().Collect().Payload!;

        Assert.Empty(payload.Apps); // port 0 / missing remote cannot enter the contract (port ≥ 1)
    }
}
