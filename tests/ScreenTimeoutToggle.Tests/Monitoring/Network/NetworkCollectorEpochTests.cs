using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>Three-level epoch semantics (contract §3.3): interface wrap/re-enumeration, app PID reuse.</summary>
public class NetworkCollectorEpochTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeInterfaceCounterTable _interfaces = new();
    private readonly FakeConnectionTable _connections = new();
    private readonly FakeIdentityProvider _identities = new();

    private WindowsNetworkCollector NewCollector(NetworkCollectorOptions? limits = null) =>
        NetFakes.Collector(_clock, _interfaces, _connections, _identities, limits);

    [Fact]
    public void CounterWrap_StartsNewInterfaceEpoch_WithUnknownRates_AndNoTruncationFlag()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 5000, tx: 9000));
        var collector = NewCollector();
        var first = collector.Collect().Payload!;
        Assert.Equal(0, first.Interfaces[0].Epoch);

        _clock.Advance(TimeSpan.FromSeconds(1));
        // Counter reset/wrap: current < previous. The new value is the epoch-1 baseline.
        _interfaces.Rows[0] = NetFakes.Iface("{GUID-A}", rx: 100, tx: 200);
        var payload = collector.Collect().Payload!;

        var iface = Assert.Single(payload.Interfaces);
        Assert.Equal(1, iface.Epoch);
        Assert.Null(iface.UpRate);   // first sample of the new epoch: unknown, never negative
        Assert.Null(iface.DownRate);
        Assert.Equal(100, iface.RxBytes);
        Assert.Equal(200, iface.TxBytes);
        // A wrap is not a truncation and not a dropped event.
        Assert.False(payload.Truncated);
        Assert.Equal(0, payload.DroppedEvents);
    }

    [Fact]
    public void NoWrap_SameCountersSameEpoch()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 100, tx: 100));
        var collector = NewCollector();
        collector.Collect();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _interfaces.Rows[0] = NetFakes.Iface("{GUID-A}", rx: 100, tx: 100); // unchanged counters are legal
        var iface = Assert.Single(collector.Collect().Payload!.Interfaces);
        Assert.Equal(0, iface.Epoch);
        Assert.Equal(0.0, iface.DownRate); // a REAL zero rate: measured, not fabricated
    }

    [Fact]
    public void InterfaceReEnumeration_AfterDisappearance_BumpsEpoch()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 1000, tx: 1000));
        var collector = NewCollector();
        collector.Collect();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _interfaces.Rows.Clear(); // interface removed
        var missing = collector.Collect().Payload!;
        Assert.Empty(missing.Interfaces);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 3000, tx: 3000));
        var iface = Assert.Single(collector.Collect().Payload!.Interfaces);
        Assert.Equal(1, iface.Epoch);
        Assert.Null(iface.UpRate);
    }

    [Fact]
    public void PidReuse_SameAppNewStartTime_BumpsAppEpoch_AndReplacesProcessIdentity()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(pid: 100, "203.0.113.10", 443));
        var collector = NewCollector();
        var first = collector.Collect().Payload!;
        var appBefore = Assert.Single(first.Apps);
        Assert.Equal(0, appBefore.Epoch);
        var pidBefore = Assert.Single(appBefore.Processes);
        var startBefore = pidBefore.StartTime;

        _clock.Advance(TimeSpan.FromSeconds(5));
        // Same pid, different startTime = the OS reused the PID for a new instance.
        _identities.ByPid[100] = FakeIdentityProvider.DefaultIdentity(100) with { StartTimeMs = startBefore + 5000 };
        var payload = collector.Collect().Payload!;

        var app = Assert.Single(payload.Apps);
        Assert.Equal(appBefore.Id, app.Id); // stable appId survives the restart
        Assert.Equal(1, app.Epoch);
        var proc = Assert.Single(app.Processes);
        Assert.Equal(100, proc.Pid);
        Assert.Equal(startBefore + 5000, proc.StartTime);
    }
}
