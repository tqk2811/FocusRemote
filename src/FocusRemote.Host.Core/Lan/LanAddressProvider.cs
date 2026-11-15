using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FocusRemote.Host.Core.Lan;

/// <summary>Local IPv4 addresses a LAN viewer can reach (up, non-loopback, non link-local).</summary>
public static class LanAddressProvider
{
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        List<IPAddress> result = [];
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    continue;
                byte[] bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;
                if (!result.Contains(address))
                    result.Add(address);
            }
        }
        return result;
    }

    /// <summary>Viewer URLs: one per IPv4 address, then the machine name.</summary>
    public static IReadOnlyList<string> BuildUrls(IEnumerable<IPAddress> addresses, string machineName, int port)
    {
        List<string> urls = addresses.Select(a => $"https://{a}:{port}/").ToList();
        urls.Add($"https://{machineName.ToLowerInvariant()}:{port}/");
        return urls;
    }
}
