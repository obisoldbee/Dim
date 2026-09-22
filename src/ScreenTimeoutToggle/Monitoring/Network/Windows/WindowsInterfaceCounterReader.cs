using System.Net.NetworkInformation;
using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>
/// Interface counter source through the BCL's dual-family path
/// (<see cref="NetworkInterface.GetIPStatistics"/>, which marshals MIB_IF_ROW2 itself).
/// InOctets/OutOctets there cover both address families, unlike
/// <see cref="NetworkInterface.GetIPv4Statistics"/>.
/// <para>
/// This replaced a hand-computed MIB_IF_ROW2 byte-offset parser: on this machine
/// GetIfTable2 returned 44 rows of an unexpected stride, so InOctets/OutOctets landed
/// inside unrelated data and produced 19-digit counters that the contract validator then
/// rejected. Same lesson as the memory page's GetPerformanceInfo pin — the layout bet is
/// not worth taking while a supported API exists.
/// </para>
/// Classification follows the frozen rule: loopback by IFTYPE, tunnel by IFTYPE or
/// name/description evidence, physical for real Ethernet/Wi-Fi types, and UNKNOWN whenever
/// the evidence is insufficient — virtual adapters are never guessed physical.
/// </summary>
public sealed class WindowsInterfaceCounterReader : IInterfaceCounterTable
{
    private const int IfTypeEthernetCsmacd = 6;
    private const int IfTypeSoftwareLoopback = 24;
    private const int IfTypeFastEtherFx = 62;
    private const int IfTypeFastEtherT = 69;
    private const int IfTypeIeee80211 = 71;
    private const int IfTypeGigabitEthernet = 117;
    private const int IfTypeTunnel = 131;

    private static readonly string[] TunnelKeywords =
        ["wintun", "tap", "tailscale", "zerotier", "openvpn", "wireguard", "ppp", "vpn"];

    private static readonly string[] VirtualKeywords =
        ["hyper-v", "vmware", "virtualbox", "vethernet", "bluetooth", "wan miniport", "virtual"];

    public NetworkReadStatus TryRead(out IReadOnlyList<InterfaceCounterRow> rows, out string? error)
    {
        rows = [];
        error = null;

        IReadOnlyList<NetworkInterface> adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException e)
        {
            error = $"GetAllNetworkInterfaces failed (win32:{e.NativeErrorCode})";
            return NetworkReadStatus.Failed;
        }

        var parsed = new List<InterfaceCounterRow>(adapters.Count);
        var unreadable = 0;
        foreach (var nic in adapters)
        {
            ulong? rx = null;
            ulong? tx = null;
            try
            {
                var stats = nic.GetIPStatistics();
                // The BCL hands back a signed long of an unsigned 64-bit counter; the bit
                // pattern is the real counter, so the reinterpreting cast is exact.
                rx = unchecked((ulong)stats.BytesReceived);
                tx = unchecked((ulong)stats.BytesSent);
            }
            catch (Exception e) when (e is NetworkInformationException or InvalidOperationException or NotSupportedException
                                      or PlatformNotSupportedException)
            {
                unreadable++; // counters unknown for this adapter — never reported as 0
            }

            var name = string.IsNullOrWhiteSpace(nic.Name) ? nic.Id : nic.Name;
            parsed.Add(new InterfaceCounterRow(
                nic.Id,
                name,
                Classify((int)nic.NetworkInterfaceType, nic.Name, nic.Description),
                rx,
                tx, nic.OperationalStatus == OperationalStatus.Up));
        }

        if (parsed.Count == 0)
        {
            error = "no network interfaces enumerated";
            return NetworkReadStatus.Failed;
        }

        rows = parsed;
        if (unreadable > 0) error = $"{unreadable} interface(s) returned no counters";
        return NetworkReadStatus.Ok;
    }

    /// <summary>
    /// Frozen classification (contract §6.4, probe report §a): evidence-based, unknown on
    /// insufficient evidence. Tunnel keywords outrank physical types; virtual keywords
    /// force unknown — a virtual NIC of unclear forwarding role is never guessed.
    /// </summary>
    internal static InterfaceKind Classify(int ifType, string alias, string description)
    {
        if (ifType == IfTypeSoftwareLoopback)
            return InterfaceKind.Loopback;

        var haystack = $"{alias} {description}".ToLowerInvariant();

        if (ifType == IfTypeTunnel || TunnelKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
            return InterfaceKind.Tunnel;

        if (VirtualKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
            return InterfaceKind.Unknown;

        if (ifType is IfTypeEthernetCsmacd or IfTypeIeee80211 or IfTypeFastEtherFx
            or IfTypeFastEtherT or IfTypeGigabitEthernet)
            return InterfaceKind.Physical;

        return InterfaceKind.Unknown;
    }
}
