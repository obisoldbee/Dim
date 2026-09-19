using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OBDim.NetworkProbe;

/// <summary>
/// Probe (b): PID-annotated connection tables via iphlpapi GetExtendedTcpTable /
/// GetExtendedUdpTable (OWNER_PID_ALL), both address families, unelevated.
/// Verifies whether rows for other users'/system processes are visible with their PIDs
/// and states. Endpoint addresses are masked in output (loopback kept; private/public
/// collapsed) so probe logs carry no real endpoints.
/// </summary>
internal static class ConnectionTableProbe
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    private const int TcpTableOwnerPidAll = 5; // TCP_TABLE_OWNER_PID_ALL
    private const int UdpTableOwnerPidAll = 1; // UDP_TABLE_OWNER_PID

    // Row sizes: MIB_TCPROW_OWNER_PID=24, MIB_TCP6ROW_OWNER_PID=56,
    // MIB_UDPROW_OWNER_PID=12, MIB_UDP6ROW_OWNER_PID=28. Parsed by offset, no struct layout bets.
    public static int Run()
    {
        ProbeContext.Header("connections");
        var allPids = new HashSet<int>();

        var tcp4 = RunTable("tcp4", AfInet, true);
        var tcp6 = RunTable("tcp6", AfInet6, true);
        var udp4 = RunTable("udp4", AfInet, false);
        var udp6 = RunTable("udp6", AfInet6, false);

        foreach (var p in tcp4.Pids.Concat(tcp6.Pids).Concat(udp4.Pids).Concat(udp6.Pids))
            allPids.Add(p);

        // Cross-check: can a normal process resolve those PIDs to process identities?
        var resolvable = 0;
        var denied = 0;
        var gone = 0;
        foreach (var pid in allPids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                _ = proc.StartTime; // touches the native handle path
                resolvable++;
            }
            catch (ArgumentException)
            {
                gone++; // row is stale (short-lived process) — evidence for PID-reuse handling
            }
            catch (Exception)
            {
                denied++;
            }
        }

        Console.WriteLine(
            $"pid-crosscheck distinct-pids={allPids.Count} resolvable={resolvable} gone={gone} access-denied={denied}");
        Console.WriteLine(
            "note: UDP tables carry local endpoints only — they cannot stand in for remote connection/flow data.");
        return 0;
    }

    internal readonly record struct TableResult(int Rows, List<int> Pids);

    /// <summary>Silent table fetch for the perf probe. Returns the row count, or -1 on error.</summary>
    internal static int RunTableQuiet(int addressFamily, bool tcp)
    {
        var rowSize = RowSize(addressFamily, tcp);
        var size = 0;
        var ret = tcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, true, addressFamily, TcpTableOwnerPidAll, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, true, addressFamily, UdpTableOwnerPidAll, 0);
        if (ret != ErrorInsufficientBuffer && ret != NoError)
            return -1;

        var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
        try
        {
            ret = tcp
                ? GetExtendedTcpTable(buffer, ref size, true, addressFamily, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(buffer, ref size, true, addressFamily, UdpTableOwnerPidAll, 0);
            return ret != NoError ? -1 : Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        static int RowSize(int af, bool isTcp) => isTcp
            ? af == AfInet ? 24 : 56
            : af == AfInet ? 12 : 28;
    }

    internal static TableResult RunTable(string label, int addressFamily, bool tcp)
    {
        var rowSize = tcp
            ? addressFamily == AfInet ? 24 : 56
            : addressFamily == AfInet ? 12 : 28;

        var size = 0;
        var ret = tcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, true, addressFamily, TcpTableOwnerPidAll, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, true, addressFamily, UdpTableOwnerPidAll, 0);
        if (ret != ErrorInsufficientBuffer && ret != NoError)
        {
            Console.WriteLine($"{label}: query-size failed win32={ret}");
            return new TableResult(-1, []);
        }

        var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
        try
        {
            ret = tcp
                ? GetExtendedTcpTable(buffer, ref size, true, addressFamily, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(buffer, ref size, true, addressFamily, UdpTableOwnerPidAll, 0);
            if (ret != NoError)
            {
                Console.WriteLine($"{label}: read failed win32={ret}");
                return new TableResult(-1, []);
            }

            var count = Marshal.ReadInt32(buffer);
            var states = new Dictionary<uint, int>();
            var pids = new List<int>();
            var samples = new List<string>();

            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * rowSize);
                uint state;
                uint pid;
                string endpoint;
                if (tcp && addressFamily == AfInet)
                {
                    state = (uint)Marshal.ReadInt32(row, 0);
                    var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 8));
                    var remoteAddr = MaskIpv4((uint)Marshal.ReadInt32(row, 12));
                    var remotePort = ConvertPort((uint)Marshal.ReadInt32(row, 16));
                    pid = (uint)Marshal.ReadInt32(row, 20);
                    endpoint = $"-> {remoteAddr}:{remotePort} local-port={localPort}";
                }
                else if (tcp)
                {
                    // MIB_TCP6ROW_OWNER_PID: 16B local, scope, port, 16B remote, scope, port, state, pid
                    var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 20));
                    var remotePort = ConvertPort((uint)Marshal.ReadInt32(row, 44));
                    state = (uint)Marshal.ReadInt32(row, 48);
                    pid = (uint)Marshal.ReadInt32(row, 52);
                    endpoint = $"-> [ipv6-masked]:{remotePort} local-port={localPort}";
                }
                else if (addressFamily == AfInet)
                {
                    state = 0;
                    var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 4));
                    pid = (uint)Marshal.ReadInt32(row, 8);
                    endpoint = $"local-port={localPort}";
                }
                else
                {
                    state = 0;
                    var localPort = ConvertPort((uint)Marshal.ReadInt32(row, 20));
                    pid = (uint)Marshal.ReadInt32(row, 24);
                    endpoint = $"[ipv6] local-port={localPort}";
                }

                if (tcp)
                    states[state] = states.GetValueOrDefault(state) + 1;
                pids.Add((int)pid);
                if (samples.Count < 5)
                    samples.Add($"pid={pid} state={(tcp ? TcpStateName(state) : "-")} {endpoint}");
            }

            Console.WriteLine($"{label}: rows={count} distinct-pids={pids.Distinct().Count()}");
            foreach (var kv in states.OrderBy(kv => kv.Key))
                Console.WriteLine($"  state {TcpStateName(kv.Key)}({kv.Key}) = {kv.Value}");
            foreach (var s in samples)
                Console.WriteLine($"  sample {s}");
            return new TableResult(count, pids);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ConvertPort(uint networkOrder) =>
        ((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF);

    private static string MaskIpv4(uint networkOrderAddr)
    {
        var b0 = networkOrderAddr & 0xFF;
        var b1 = (networkOrderAddr >> 8) & 0xFF;
        if (b0 == 127) return "127.0.0.1";
        if (b0 == 10 || (b0 == 172 && b1 is >= 16 and <= 31) || (b0 == 192 && b1 == 168))
            return "private-masked";
        if (b0 == 0 || b0 >= 224) return "special-masked";
        return "public-masked";
    }

    private static string TcpStateName(uint state) => state switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RCVD",
        5 => "ESTABLISHED", 6 => "FIN_WAIT1", 7 => "FIN_WAIT2", 8 => "CLOSE_WAIT",
        9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB",
        _ => "UNKNOWN",
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ulAf, int tableClass, uint reserved);
}
