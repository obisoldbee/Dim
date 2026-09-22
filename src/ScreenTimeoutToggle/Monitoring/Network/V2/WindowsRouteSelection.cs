using System.Net;
using System.Runtime.InteropServices;

namespace OBDim.Monitoring.Network.V2;

/// <summary>OS best-route lookup only. No socket, DNS lookup, ping or packet is sent.</summary>
internal static class WindowsRouteSelection
{
    internal static string? Resolve()
    {
        foreach (var address in new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111") })
        {
            var v4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
            var socketAddress = new byte[v4 ? 16 : 28];
            socketAddress[0] = (byte)(v4 ? 2 : 23);
            address.GetAddressBytes().CopyTo(socketAddress, v4 ? 4 : 8);
            if (GetBestInterfaceEx(socketAddress, out var index) != 0) continue;
            if (ConvertInterfaceIndexToLuid(index, out var luid) != 0) continue;
            if (ConvertInterfaceLuidToGuid(ref luid, out var guid) == 0) return guid.ToString("B");
        }
        return null;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx(byte[] address, out uint index);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceLuidToGuid(ref ulong luid, out Guid guid);
}
