using System.Diagnostics;
using OBDim.Monitoring.Network;
using OBDim.Monitoring.Network.Json;
using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Validation;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>
/// Real-machine sanity (same discipline as WindowsMemoryReaderTests): the Windows
/// readers return physically plausible data without elevation, and the fully wired
/// service produces a contract-valid snapshot. These tests exercise the P/Invoke
/// layouts for real — a wrong offset shows up here, not in production.
/// </summary>
public class WindowsNetworkIntegrationTests
{
    [Fact]
    public void InterfaceReader_RealMachine_ReturnsPlausibleCounters()
    {
        // Reference first, subject second: counters only grow within an epoch, so reading
        // the IPv4 baseline afterwards compares a later sample against an earlier one and
        // the superset assertion loses by a few hundred bytes on a busy link.
        var ipv4Total = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Sum(n => { try { return n.GetIPv4Statistics().BytesReceived; } catch { return 0; } });

        var reader = new WindowsInterfaceCounterReader();
        var status = reader.TryRead(out var rows, out var error);

        Assert.Equal(NetworkReadStatus.Ok, status);
        Assert.Null(error);
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.StartsWith("{", row.Id); // InterfaceGuid identity (contract §6.4)
            Assert.False(string.IsNullOrWhiteSpace(row.Name));
            Assert.True(Enum.IsDefined(row.Kind));
            // The regression this pins: a bad struct read yields 19-digit junk, not a
            // counter the contract can carry.
            Assert.True(row.RxBytes is null || row.RxBytes <= (ulong)NetworkSnapshotValidator.MaxSafeInt,
                $"rxBytes {row.RxBytes} is outside the contract range");
            Assert.True(row.TxBytes is null || row.TxBytes <= (ulong)NetworkSnapshotValidator.MaxSafeInt,
                $"txBytes {row.TxBytes} is outside the contract range");
        });
        Assert.Contains(rows, r => r.Kind == InterfaceKind.Loopback);
        Assert.Contains(rows, r => r.Kind == InterfaceKind.Physical && r.RxBytes > 0);

        // The dual-family counters must never be behind the IPv4-only reading the N0 probe used.
        var tableTotal = rows.Sum(r => r.RxBytes is null ? 0L : (long)Math.Min(r.RxBytes.Value, (ulong)long.MaxValue));
        Assert.True(tableTotal >= ipv4Total, $"dual-family total {tableTotal} < IPv4-only total {ipv4Total}");
    }

    [Fact]
    public void InterfaceClassification_KnownShapes()
    {
        Assert.Equal(InterfaceKind.Loopback, WindowsInterfaceCounterReader.Classify(24, "Loopback Pseudo-Interface 1", ""));
        Assert.Equal(InterfaceKind.Tunnel, WindowsInterfaceCounterReader.Classify(131, "Wintun0", "Wintun Tunnel"));
        Assert.Equal(InterfaceKind.Tunnel, WindowsInterfaceCounterReader.Classify(6, "Ethernet 2", "TAP-Windows Adapter V9"));
        Assert.Equal(InterfaceKind.Physical, WindowsInterfaceCounterReader.Classify(6, "Ethernet", "Intel(R) Ethernet Controller"));
        Assert.Equal(InterfaceKind.Physical, WindowsInterfaceCounterReader.Classify(71, "WLAN", "Realtek 8822CE Wireless LAN"));
        // Virtual adapters of unclear role stay unknown — never guessed (probe report §a).
        Assert.Equal(InterfaceKind.Unknown, WindowsInterfaceCounterReader.Classify(71, "本地连接* 9", "Microsoft Wi-Fi Direct Virtual Adapter"));
        Assert.Equal(InterfaceKind.Unknown, WindowsInterfaceCounterReader.Classify(6, "蓝牙网络连接", "Bluetooth Device (Personal Area Network)"));
        Assert.Equal(InterfaceKind.Unknown, WindowsInterfaceCounterReader.Classify(9999, "mystery", "undocumented"));
    }

    [Fact]
    public void ConnectionReader_RealMachine_TcpRowsCarryPidsAndEndpoints()
    {
        var reader = new WindowsConnectionTableReader();
        var status = reader.TryReadTcp(out var rows, out _);

        Assert.Equal(NetworkReadStatus.Ok, status);
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.True(row.Pid >= 0);
            Assert.Equal(NetworkProtocol.Tcp, row.Protocol);
            Assert.False(string.IsNullOrWhiteSpace(row.State));
        });
        Assert.Contains(rows, r => r.State == "LISTEN" || r.State == "ESTABLISHED");
        // UDP tables are capability evidence: readable, local-endpoint-only.
        Assert.Equal(NetworkReadStatus.Ok, reader.TryReadUdp(out var udpRows, out _));
        Assert.All(udpRows, r => Assert.Null(r.RemoteAddress));
    }

    [Fact]
    public void IdentityResolver_RealMachine_ResolvesCurrentProcess()
    {
        var resolver = new ProcessIdentityResolver();
        resolver.BeginCycle();

        var info = resolver.Resolve(Environment.ProcessId);

        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessId, info!.Pid);
        Assert.False(string.IsNullOrWhiteSpace(info.Name));
        var expected = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        Assert.True(Math.Abs(info.StartTimeMs - expected) < 60_000,
            $"resolver startTime {info.StartTimeMs} vs Process API {expected}");
    }

    [Fact]
    public void Service_RealMachine_OneCollect_ProducesContractValidSnapshot()
    {
        using var service = NetworkMonitorService.CreateDefault();
        service.Start();
        service.CollectOnce();
        service.Stop();

        var snapshot = service.LastGoodDataSnapshot ?? service.Current;
        Assert.Equal(SnapshotOrigin.Host, snapshot.Origin);
        Assert.True(snapshot.Coverage is NetworkCoverage.Active or NetworkCoverage.Partial,
            $"unexpected coverage {snapshot.Coverage}");
        Assert.NotEmpty(snapshot.Interfaces); // a real machine always has interfaces

        var errors = NetworkSnapshotValidator.Validate(NetworkSnapshotJson.Serialize(snapshot));
        Assert.True(errors.Count == 0, string.Join("; ", errors));
    }
}
