using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Windows;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>Interface-level fakes over the collector seams — no fixture-file fallbacks, no OS calls.</summary>
internal sealed class FakeInterfaceCounterTable : IInterfaceCounterTable
{
    public NetworkReadStatus Status = NetworkReadStatus.Ok;
    public string? Error;
    public List<InterfaceCounterRow> Rows = [];

    public NetworkReadStatus TryRead(out IReadOnlyList<InterfaceCounterRow> rows, out string? error)
    {
        rows = Status == NetworkReadStatus.Ok ? Rows : [];
        error = Error;
        return Status;
    }
}

internal sealed class FakeConnectionTable : IConnectionTable
{
    public NetworkReadStatus TcpStatus = NetworkReadStatus.Ok;
    public NetworkReadStatus UdpStatus = NetworkReadStatus.Ok;
    public List<ConnectionRow> TcpRows = [];
    public bool ThrowOnRead;

    public NetworkReadStatus TryReadTcp(out IReadOnlyList<ConnectionRow> rows, out string? error)
    {
        if (ThrowOnRead) throw new InvalidOperationException("simulated connection table crash");
        rows = TcpStatus == NetworkReadStatus.Ok ? TcpRows : [];
        error = null;
        return TcpStatus;
    }

    public NetworkReadStatus TryReadUdp(out IReadOnlyList<ConnectionRow> rows, out string? error)
    {
        if (ThrowOnRead) throw new InvalidOperationException("simulated connection table crash");
        rows = [];
        error = null;
        return UdpStatus;
    }
}

internal sealed class FakeIdentityProvider : IProcessIdentityProvider
{
    /// <summary>Pids present here resolve to the given identity (null = unresolvable). Others get a stable default.</summary>
    public Dictionary<int, ProcessIdentityInfo?> ByPid = [];

    public void BeginCycle() { }

    public ProcessIdentityInfo? Resolve(int pid)
    {
        if (ByPid.TryGetValue(pid, out var info)) return info;
        return DefaultIdentity(pid);
    }

    public static ProcessIdentityInfo DefaultIdentity(int pid) =>
        new(pid, StartTimeMs: 1_789_900_000_000 + pid, $"app{pid}.exe",
            ParentPid: 1, ExecutablePath: $"C:\\Apps\\app{pid}\\app{pid}.exe");
}

/// <summary>Row builders and wiring helpers shared by the network tests.</summary>
internal static class NetFakes
{
    public static InterfaceCounterRow Iface(
        string id, ulong rx, ulong tx, InterfaceKind kind = InterfaceKind.Physical, string? name = null) =>
        new(id, name ?? $"nic-{id}", kind, rx, tx);

    public static ConnectionRow Tcp(
        int pid, string remoteIp, int remotePort,
        string state = "ESTABLISHED", string localIp = "192.168.1.10", int localPort = 50000) =>
        new(NetworkProtocol.Tcp, localIp, localPort, remoteIp, remotePort, state, pid);

    public static WindowsNetworkCollector Collector(
        FakeClock clock,
        FakeInterfaceCounterTable interfaces,
        FakeConnectionTable connections,
        FakeIdentityProvider identities,
        NetworkCollectorOptions? limits = null) =>
        new(clock, interfaces, connections, identities, limits);

    public static (FakeInterfaceCounterTable, FakeConnectionTable, FakeIdentityProvider) HappySeams() =>
        (new FakeInterfaceCounterTable(), new FakeConnectionTable(), new FakeIdentityProvider());
}
