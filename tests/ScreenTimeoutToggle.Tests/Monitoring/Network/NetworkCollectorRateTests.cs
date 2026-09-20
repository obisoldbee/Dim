using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Validation;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>Rate math: real elapsed time, first-sample unknowns, decimal bytes/second.</summary>
public class NetworkCollectorRateTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeInterfaceCounterTable _interfaces = new();
    private readonly FakeConnectionTable _connections = new();
    private readonly FakeIdentityProvider _identities = new();

    private WindowsNetworkCollector.CollectPayload Collect()
    {
        var result = NetFakes.Collector(_clock, _interfaces, _connections, _identities).Collect();
        Assert.Equal(WindowsNetworkCollector.CollectStatus.Ok, result.Status);
        return result.Payload!;
    }

    private WindowsNetworkCollector NewCollector() =>
        NetFakes.Collector(_clock, _interfaces, _connections, _identities);

    [Fact]
    public void FirstSample_RatesAreNull_NeverZero()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 1000, tx: 2000));
        var collector = NewCollector();

        var payload = collector.Collect().Payload!;

        var iface = Assert.Single(payload.Interfaces);
        Assert.Null(iface.UpRate);
        Assert.Null(iface.DownRate);
        // Cumulative counters are real from the first sample on.
        Assert.Equal(1000, iface.RxBytes);
        Assert.Equal(2000, iface.TxBytes);
        // The first history point exists (the instant is known) with unknown rates.
        var point = Assert.Single(iface.History);
        Assert.Null(point.Up);
        Assert.Null(point.Down);
    }

    [Fact]
    public void SecondSample_RateIsDeltaOverRealElapsedSeconds()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 1000, tx: 2000));
        var collector = NewCollector();
        collector.Collect();

        _clock.Advance(TimeSpan.FromSeconds(2));
        _interfaces.Rows[0] = NetFakes.Iface("{GUID-A}", rx: 1400, tx: 2600);
        var payload = collector.Collect().Payload!;

        var iface = Assert.Single(payload.Interfaces);
        Assert.Equal(200.0, iface.DownRate); // 400 bytes / 2 s
        Assert.Equal(300.0, iface.UpRate);   // 600 bytes / 2 s
        Assert.Equal(2, iface.History.Count);
        Assert.True(iface.History[0].T < iface.History[1].T, "history timestamps strictly increase (S3)");
    }

    [Fact]
    public void SameTimestampSample_YieldsUnknownRates_NotDivisionByZero()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 1000, tx: 2000));
        var collector = NewCollector();
        collector.Collect();

        // No clock advance: dt = 0 must never fabricate an infinite/zero rate.
        _interfaces.Rows[0] = NetFakes.Iface("{GUID-A}", rx: 1500, tx: 2500);
        var payload = collector.Collect().Payload!;

        var iface = Assert.Single(payload.Interfaces);
        Assert.Null(iface.UpRate);
        Assert.Null(iface.DownRate);
        Assert.Single(iface.History); // same-ms re-read replaces, never duplicates
    }

    [Fact]
    public void RatesAreDecimalBytesPerSecond_NotBits()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 0, tx: 0));
        var collector = NewCollector();
        collector.Collect();

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        _interfaces.Rows[0] = NetFakes.Iface("{GUID-A}", rx: 500, tx: 100);
        var iface = Assert.Single(collector.Collect().Payload!.Interfaces);

        // 500 bytes / 0.5 s = 1000 B/s — decimal bytes, never bit/s.
        Assert.Equal(1000.0, iface.DownRate);
        Assert.Equal(200.0, iface.UpRate);
    }

    [Fact]
    public void CounterBeyondContractBound_DegradesToUnknown_NotAnInvalidSnapshot()
    {
        // The contract caps JSON integers at 2^53-1. A counter past that bound must become
        // unknown instead of emitting a snapshot the validator rejects (the failure mode a
        // wrong MIB_IF_ROW2 offset produced on a real machine).
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}",
            rx: (ulong)NetworkSnapshotValidator.MaxSafeInt + 1,
            tx: (ulong)NetworkSnapshotValidator.MaxSafeInt));
        var collector = NewCollector();

        var iface = Assert.Single(collector.Collect().Payload!.Interfaces);

        Assert.Null(iface.RxBytes);
        Assert.Equal((long?)NetworkSnapshotValidator.MaxSafeInt, iface.TxBytes);
    }
}
