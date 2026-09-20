using OBDim.Monitoring.Network;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>Contract §9 resource bounds: list caps, history dual limit, string caps, truncated flag.</summary>
public class NetworkSnapshotBoundsTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeInterfaceCounterTable _interfaces = new();
    private readonly FakeConnectionTable _connections = new();
    private readonly FakeIdentityProvider _identities = new();

    private WindowsNetworkCollector NewCollector(NetworkCollectorOptions limits) =>
        NetFakes.Collector(_clock, _interfaces, _connections, _identities, limits);

    private static NetworkCollectorOptions Tiny() => NetworkCollectorOptions.Default with
    {
        MaxInterfaces = 3,
        MaxApps = 2,
        MaxConnectionsPerApp = 2,
        MaxProcessesPerApp = 1,
        HistoryMaxPoints = 3,
        HistoryWindowMs = 2500,
    };

    [Fact]
    public void InterfacesOverLimit_AreCut_AndFlaggedTruncated()
    {
        for (var i = 0; i < 5; i++)
            _interfaces.Rows.Add(NetFakes.Iface($"{{GUID-{i}}}", rx: 1, tx: 1));
        var payload = NewCollector(Tiny()).Collect().Payload!;

        Assert.Equal(3, payload.Interfaces.Count);
        Assert.True(payload.Truncated);
    }

    [Fact]
    public void AppsOverLimit_AreCut_AndFlaggedTruncated()
    {
        for (var pid = 100; pid < 103; pid++)
            _connections.TcpRows.Add(NetFakes.Tcp(pid, $"203.0.113.{pid}", 443));
        var payload = NewCollector(Tiny()).Collect().Payload!;

        Assert.Equal(2, payload.Apps.Count);
        Assert.True(payload.Truncated);
    }

    [Fact]
    public void ConnectionsPerAppOverLimit_AreCut_AndFlaggedTruncated()
    {
        _connections.TcpRows.Add(NetFakes.Tcp(100, "203.0.113.10", 443));
        _connections.TcpRows.Add(NetFakes.Tcp(100, "203.0.113.11", 443, localPort: 50001));
        var payload = NewCollector(Tiny()).Collect().Payload!;

        var app = Assert.Single(payload.Apps);
        Assert.Equal(2, app.Connections.Count); // 4 directional observations cut to the cap
        Assert.True(payload.Truncated);
    }

    [Fact]
    public void ProcessesPerAppOverLimit_AreCut_AndFlaggedTruncated()
    {
        // Two pids, same executable path → one app with two process instances.
        _identities.ByPid[100] = new ProcessIdentityInfo(100, 1_789_900_000_000, "appA.exe", 1, "C:\\Apps\\A\\appA.exe");
        _identities.ByPid[101] = new ProcessIdentityInfo(101, 1_789_900_100_000, "appA.exe", 1, "C:\\Apps\\A\\appA.exe");
        _connections.TcpRows.Add(NetFakes.Tcp(100, "203.0.113.10", 443));
        _connections.TcpRows.Add(NetFakes.Tcp(101, "203.0.113.10", 443, localPort: 50001));
        var payload = NewCollector(Tiny()).Collect().Payload!;

        var app = Assert.Single(payload.Apps);
        Assert.Single(app.Processes);
        Assert.True(payload.Truncated);
    }

    [Fact]
    public void HistoryDualBound_CountAndWindow_CutOldest_AndFlagTruncated()
    {
        _interfaces.Rows.Add(NetFakes.Iface("{GUID-A}", rx: 0, tx: 0));
        var collector = NewCollector(Tiny());

        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var payload = collector.Collect().Payload!;
            var history = Assert.Single(payload.Interfaces).History;
            Assert.True(history.Count <= 3, $"count bound: {history.Count} ≤ 3");
            var nowMs = _clock.UtcNow.ToUnixTimeMilliseconds();
            Assert.All(history, p => Assert.True(p.T >= nowMs - 2500, "window bound"));
            if (i >= 3) Assert.True(payload.Truncated, "trimmed history flags the snapshot");
        }
    }

    [Fact]
    public void LongStrings_AreCutToSchemaLengths_ByTheBounder()
    {
        var snapshot = new NetworkSnapshot
        {
            SchemaVersion = 1,
            Origin = SnapshotOrigin.Host,
            AsOf = 1000,
            CaptureStartedAt = 0,
            Epoch = 0,
            Sequence = 0,
            Coverage = NetworkCoverage.Active,
            CoverageReasons = [],
            Capabilities = NetworkCapabilities.ReadOnly(true, true),
            DroppedEvents = 0,
            Truncated = false,
            Apps =
            [
                new AppObservation
                {
                    Id = new string('a', 200),
                    Name = new string('n', 600),
                    BundleId = null,
                    Path = new string('p', 1500),
                    IdentitySource = "executable-path",
                    UploadBytes = null, DownloadBytes = null, UpRate = null, DownRate = null,
                    ConnectionCount = 0, Epoch = 0,
                    Connections = [], History = [],
                    Processes =
                    [
                        new ObservedProcessIdentity
                        {
                            Pid = 1, StartTime = 1, Name = new string('x', 300),
                            ParentPid = null, ExecutablePath = null,
                        },
                    ],
                },
            ],
            Interfaces = [],
        };

        var bounded = NetworkSnapshotBounder.Enforce(snapshot);

        Assert.True(bounded.Truncated);
        var app = bounded.Apps[0];
        Assert.Equal(128, app.Id.Length);
        Assert.Equal(512, app.Name.Length);
        Assert.Equal(1024, app.Path!.Length);
        Assert.Equal(256, app.Processes[0].Name.Length);
    }

    [Fact]
    public void NonDataSnapshots_PassThroughUntouched()
    {
        var stopped = NetworkSnapshot.Lifecycle(
            NetworkCoverage.Stopped, CoverageReason.UserDisabled, 1000, 1000, 0, 0);
        Assert.Same(stopped, NetworkSnapshotBounder.Enforce(stopped));
    }
}
