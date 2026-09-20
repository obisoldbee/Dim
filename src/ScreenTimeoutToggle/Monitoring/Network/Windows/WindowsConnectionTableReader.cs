using System.Net;
using System.Runtime.InteropServices;
using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>
/// PID-annotated connection tables via iphlpapi GetExtendedTcpTable / GetExtendedUdpTable
/// (OWNER_PID_ALL, both address families) — the path the N0 probe verified readable
/// without elevation (probe report §b: full-system rows with PIDs and TCP states).
/// Rows are parsed by explicit byte offsets; no struct layout bets.
/// <para>
/// UDP tables carry LOCAL endpoints only (probe report §b): they are capability evidence
/// and can never stand in for remote connection/flow data, so UDP rows are returned with
/// null remote fields and the collector must not emit contract connections from them.
/// </para>
/// </summary>
public sealed class WindowsConnectionTableReader : IConnectionTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInsufficientBuffer = 122;

    private const int TcpTableOwnerPidAll = 5; // TCP_TABLE_OWNER_PID_ALL
    private const int UdpTableOwnerPid = 1;    // UDP_TABLE_OWNER_PID

    public NetworkReadStatus TryReadTcp(out IReadOnlyList<ConnectionRow> rows, out string? error) =>
        ReadBothFamilies(tcp: true, out rows, out error);

    public NetworkReadStatus TryReadUdp(out IReadOnlyList<ConnectionRow> rows, out string? error) =>
        ReadBothFamilies(tcp: false, out rows, out error);

    private static NetworkReadStatus ReadBothFamilies(
        bool tcp, out IReadOnlyList<ConnectionRow> rows, out string? error)
    {
        var collected = new List<ConnectionRow>();
        var failures = new List<string>();
        var denied = 0;

        foreach (var af in new[] { AfInet, AfInet6 })
        {
            var status = ReadFamily(tcp, af, collected, out var familyError);
            if (status == NetworkReadStatus.AccessDenied)
                denied++;
            else if (status == NetworkReadStatus.Failed)
                failures.Add(familyError ?? $"af={af} failed");
        }

        if (denied == 2)
        {
            rows = [];
            error = $"{(tcp ? "TCP" : "UDP")} owner-PID tables access denied";
            return NetworkReadStatus.AccessDenied;
        }
        if (denied == 1 || failures.Count == 2)
        {
            // One family denied or both families broken: without a usable table there is
            // no honest partial row set to offer.
            rows = [];
            error = $"{(tcp ? "TCP" : "UDP")} owner-PID table unreadable: " +
                    (denied == 1 ? "one address family access denied" : string.Join("; ", failures));
            return denied == 1 ? NetworkReadStatus.AccessDenied : NetworkReadStatus.Failed;
        }

        rows = collected;
        // One family failed but the other read fine: partial evidence, error describes the gap.
        error = failures.Count == 0 ? null : string.Join("; ", failures);
        return NetworkReadStatus.Ok;
    }

    private static NetworkReadStatus ReadFamily(
        bool tcp, int addressFamily, List<ConnectionRow> collected, out string? error)
    {
        error = null;
        var rowSize = tcp
            ? addressFamily == AfInet ? 24 : 56
            : addressFamily == AfInet ? 12 : 28;

        var size = 0;
        var ret = tcp
            ? Native.GetExtendedTcpTable(IntPtr.Zero, ref size, true, addressFamily, TcpTableOwnerPidAll, 0)
            : Native.GetExtendedUdpTable(IntPtr.Zero, ref size, true, addressFamily, UdpTableOwnerPid, 0);
        if (ret == ErrorAccessDenied)
        {
            error = $"query-size access denied (af={addressFamily})";
            return NetworkReadStatus.AccessDenied;
        }
        if (ret != ErrorInsufficientBuffer && ret != NoError)
        {
            error = $"query-size failed win32={ret} (af={addressFamily})";
            return NetworkReadStatus.Failed;
        }

        var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
        try
        {
            ret = tcp
                ? Native.GetExtendedTcpTable(buffer, ref size, true, addressFamily, TcpTableOwnerPidAll, 0)
                : Native.GetExtendedUdpTable(buffer, ref size, true, addressFamily, UdpTableOwnerPid, 0);
            if (ret == ErrorAccessDenied)
            {
                error = $"read access denied (af={addressFamily})";
                return NetworkReadStatus.AccessDenied;
            }
            if (ret != NoError)
            {
                error = $"read failed win32={ret} (af={addressFamily})";
                return NetworkReadStatus.Failed;
            }

            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + (i * rowSize));
                collected.Add(ParseRow(tcp, addressFamily, row));
            }
            return NetworkReadStatus.Ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Row layouts (probe-verified): MIB_TCPROW_OWNER_PID=24, MIB_TCP6ROW_OWNER_PID=56,
    // MIB_UDPROW_OWNER_PID=12, MIB_UDP6ROW_OWNER_PID=28.
    private static ConnectionRow ParseRow(bool tcp, int addressFamily, IntPtr row)
    {
        if (tcp && addressFamily == AfInet)
        {
            var state = (uint)Marshal.ReadInt32(row, 0);
            var localAddr = ReadIpv4(row, 4);
            var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 8));
            var remoteAddr = ReadIpv4(row, 12);
            var remotePort = ConvertPort((uint)Marshal.ReadInt32(row, 16));
            var pid = Marshal.ReadInt32(row, 20);
            return new ConnectionRow(NetworkProtocol.Tcp, localAddr, (int)localPort,
                remoteAddr, (int)remotePort, TcpStateName(state), pid);
        }
        if (tcp)
        {
            var localAddr = ReadIpv6(row, 0);
            var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 20));
            var remoteAddr = ReadIpv6(row, 24);
            var remotePort = ConvertPort((uint)Marshal.ReadInt32(row, 44));
            var state = (uint)Marshal.ReadInt32(row, 48);
            var pid = Marshal.ReadInt32(row, 52);
            return new ConnectionRow(NetworkProtocol.Tcp, localAddr, (int)localPort,
                remoteAddr, (int)remotePort, TcpStateName(state), pid);
        }
        if (addressFamily == AfInet)
        {
            var localAddr = ReadIpv4(row, 0);
            var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 4));
            var pid = Marshal.ReadInt32(row, 8);
            return new ConnectionRow(NetworkProtocol.Udp, localAddr, (int)localPort, null, null, "", pid);
        }
        else
        {
            var localAddr = ReadIpv6(row, 0);
            var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 20));
            var pid = Marshal.ReadInt32(row, 24);
            return new ConnectionRow(NetworkProtocol.Udp, localAddr, (int)localPort, null, null, "", pid);
        }
    }

    private static uint ConvertPort(uint networkOrder) =>
        ((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF);

    private static string ReadIpv4(IntPtr row, int offset)
    {
        var bytes = new byte[4];
        Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, 4);
        return new IPAddress(bytes).ToString();
    }

    private static string ReadIpv6(IntPtr row, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, 16);
        return new IPAddress(bytes).ToString();
    }

    private static string TcpStateName(uint state) => state switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RCVD",
        5 => "ESTABLISHED", 6 => "FIN_WAIT1", 7 => "FIN_WAIT2", 8 => "CLOSE_WAIT",
        9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB",
        _ => "UNKNOWN",
    };

    private static class Native
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ulAf, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ulAf, int tableClass, uint reserved);
    }
}
