using System.Text.Json;
using OBDim.Monitoring.Network;
using OBDim.Monitoring.Network.Json;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Validation;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>
/// Contract conformance of the collector's own output: S1-S8 validation, unknown≠0,
/// JSON number shapes, complete-replacement semantics, sequence monotonicity.
/// </summary>
public class NetworkSnapshotContractTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly FakeInterfaceCounterTable _interfaces = new();
    private readonly FakeConnectionTable _connections = new();
    private readonly FakeIdentityProvider _identities = new();
    private NetworkMonitorService? _service;

    private NetworkMonitorService Service()
    {
        if (_service is not null) return _service;
        _interfaces.Rows.Add(NetFakes.Iface("{1A2B3C4D-0000-4000-8000-ETH0OBDIM001}", rx: 1000, tx: 2000,
            InterfaceKind.Physical, "Ethernet0"));
        _interfaces.Rows.Add(NetFakes.Iface("{1A2B3C4D-0000-4000-8000-TUN0OBDIM001}", rx: 10, tx: 20,
            InterfaceKind.Tunnel, "Wintun0"));
        _connections.TcpRows.Add(NetFakes.Tcp(100, "203.0.113.10", 443));
        _connections.TcpRows.Add(NetFakes.Tcp(0, "203.0.113.20", 445));
        _service = new NetworkMonitorService(
            _clock,
            NetFakes.Collector(_clock, _interfaces, _connections, _identities),
            TimeSpan.FromHours(1));
        return _service;
    }

    public void Dispose() => _service?.Dispose();

    [Fact]
    public void CollectorOutput_Serialized_PassesTheFullContractValidator()
    {
        var service = Service();
        service.Start();
        service.CollectOnce();
        _clock.Advance(TimeSpan.FromSeconds(1));
        service.CollectOnce(); // second sample: rates populated

        var json = NetworkSnapshotJson.Serialize(service.Current);
        var errors = NetworkSnapshotValidator.Validate(json);

        Assert.True(errors.Count == 0, string.Join("; ", errors));
    }

    [Fact]
    public void LifecycleSnapshots_AllSixStates_PassTheValidator()
    {
        // stopped (default), starting, active via the service; the rest via the factory —
        // the same shapes the fixtures pin down.
        var service = Service();
        AssertValid(service.Current); // stopped

        service.Start();
        AssertValid(service.Current); // starting

        service.CollectOnce();
        AssertValid(service.Current); // active

        _connections.TcpStatus = OBDim.Monitoring.Network.Windows.NetworkReadStatus.AccessDenied;
        service.CollectOnce();
        AssertValid(service.Current); // denied

        _connections.TcpStatus = OBDim.Monitoring.Network.Windows.NetworkReadStatus.Ok;
        _connections.ThrowOnRead = true;
        service.CollectOnce();
        AssertValid(service.Current); // failure keeps last-good: still a valid active snapshot

        service.Stop();
        AssertValid(service.Current); // stopped again

        var disconnected = NetworkSnapshot.Lifecycle(
            NetworkCoverage.Disconnected, CoverageReason.HostUnreachable, 1000, 1000, 0, 0);
        AssertValid(disconnected);

        static void AssertValid(NetworkSnapshot snapshot)
        {
            var errors = NetworkSnapshotValidator.Validate(NetworkSnapshotJson.Serialize(snapshot));
            Assert.True(errors.Count == 0, $"{snapshot.Coverage}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void UnknownIsJsonNull_NeverZero()
    {
        var service = Service();
        service.Start();
        service.CollectOnce(); // first sample: rates unknown; app bytes always unknown in N2

        using var doc = JsonDocument.Parse(NetworkSnapshotJson.Serialize(service.Current));
        var app = doc.RootElement.GetProperty("apps")[0];
        Assert.Equal(JsonValueKind.Null, app.GetProperty("uploadBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, app.GetProperty("downloadBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, app.GetProperty("upRate").ValueKind);
        var conn = app.GetProperty("connections")[0];
        Assert.Equal(JsonValueKind.Null, conn.GetProperty("bytes").ValueKind);
        var iface = doc.RootElement.GetProperty("interfaces")[0];
        Assert.Equal(JsonValueKind.Null, iface.GetProperty("upRate").ValueKind);
        // …while the real counters are JSON integers, not strings and not nulls.
        Assert.Equal(1000, iface.GetProperty("rxBytes").GetInt64());
    }

    [Fact]
    public void RatesAreJsonNumbers_CumulativeAreJsonIntegers()
    {
        var service = Service();
        service.Start();
        service.CollectOnce();
        _clock.Advance(TimeSpan.FromSeconds(2));
        _interfaces.Rows[0] = NetFakes.Iface("{1A2B3C4D-0000-4000-8000-ETH0OBDIM001}", rx: 3000, tx: 6000,
            InterfaceKind.Physical, "Ethernet0");
        service.CollectOnce();

        using var doc = JsonDocument.Parse(NetworkSnapshotJson.Serialize(service.Current));
        var iface = doc.RootElement.GetProperty("interfaces")[0];
        Assert.Equal(JsonValueKind.Number, iface.GetProperty("downRate").ValueKind);
        Assert.Equal(1000.0, iface.GetProperty("downRate").GetDouble());
        Assert.True(iface.GetProperty("rxBytes").TryGetInt64(out _));
    }

    [Fact]
    public void CompleteReplacement_SnapshotsShareNoMutableState()
    {
        var service = Service();
        service.Start();
        service.CollectOnce();
        var first = service.Current;
        _clock.Advance(TimeSpan.FromSeconds(1));
        service.CollectOnce();
        var second = service.Current;

        Assert.NotSame(first, second);
        Assert.NotSame(first.Apps, second.Apps);
        Assert.NotSame(first.Interfaces, second.Interfaces);
        Assert.NotSame(first.Interfaces[0].History, second.Interfaces[0].History);
        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.Equal(first.Epoch, second.Epoch); // same session: sequence advances, epoch stays
    }

    [Fact]
    public void RoundTrip_SerializeDeserialize_PreservesShape()
    {
        var service = Service();
        service.Start();
        service.CollectOnce();

        var clone = NetworkSnapshotJson.Deserialize(NetworkSnapshotJson.Serialize(service.Current));

        Assert.Equal(service.Current.Coverage, clone.Coverage);
        Assert.Equal(service.Current.Apps.Count, clone.Apps.Count);
        Assert.Equal(service.Current.Interfaces.Count, clone.Interfaces.Count);
        Assert.Equal(service.Current.Apps[0].Id, clone.Apps[0].Id);
        Assert.Equal(service.Current.Apps[0].Connections[0].Protocol, clone.Apps[0].Connections[0].Protocol);
        Assert.Equal(service.Current.Apps[0].Processes[0].StartTime, clone.Apps[0].Processes[0].StartTime);
    }
}
