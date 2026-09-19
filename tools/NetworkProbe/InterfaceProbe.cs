using System.Net.NetworkInformation;

namespace OBDim.NetworkProbe;

/// <summary>
/// Probe (a): interface counters via System.Net.NetworkInformation.
/// Verifies that byte counters are readable without elevation, lists every interface,
/// and exercises the physical/tunnel/loopback/unknown classification the contract requires.
/// Caveat recorded for the report: GetIPv4Statistics covers IPv4 traffic only;
/// GetIfTable2 (P/Invoke) is the alternative when per-family accuracy matters.
/// </summary>
internal static class InterfaceProbe
{
    private static readonly string[] TunnelKeywords =
        ["wintun", "tap", "tailscale", "zerotier", "openvpn", "wireguard", "ppp", "vpn"];

    private static readonly string[] VirtualKeywords =
        ["hyper-v", "vmware", "virtualbox", "vethernet", "bluetooth", "wan miniport", "virtual"];

    public static int Run()
    {
        ProbeContext.Header("interfaces");
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: GetAllNetworkInterfaces threw {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"interface-count={nics.Length}");
        var countersReadable = 0;
        var countersFailed = 0;

        foreach (var nic in nics)
        {
            var kind = Classify(nic);
            // The machine's real adapter names are descriptive but not private data;
            // the report still quotes only kinds/counts and generic samples.
            Console.WriteLine(
                $"nic name=\"{nic.Name}\" type={nic.NetworkInterfaceType} status={nic.OperationalStatus} " +
                $"speed-bps={nic.Speed} kind={kind}");

            try
            {
                var stats = nic.GetIPv4Statistics();
                Console.WriteLine(
                    $"  ipv4-counters rx-bytes={stats.BytesReceived} tx-bytes={stats.BytesSent}");
                countersReadable++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ipv4-counters unavailable: {ex.GetType().Name}");
                countersFailed++;
            }
        }

        Console.WriteLine($"counters-readable={countersReadable} counters-failed={countersFailed}");
        Console.WriteLine(
            "note: GetIPv4Statistics is IPv4-only; GetIfTable2 (iphlpapi) covers both families and is the fallback.");
        return 0;
    }

    private static string Classify(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            return "loopback";

        var haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
            TunnelKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
            return "tunnel";

        if (VirtualKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
            return "unknown"; // virtual NICs of unclear forwarding role stay unknown, never guessed

        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT)
            return "physical";

        return "unknown";
    }
}
