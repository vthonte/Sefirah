using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sefirah.Helpers;

public static class NetworkHelper
{
    public static List<IPAddressInfo> GetAllValidAddresses()
    {
        var addresses = new List<IPAddressInfo>();
        
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus is OperationalStatus.Up)
            {
                var gateway = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily is AddressFamily.InterNetwork)?.Address;
                var isPhysical = ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet;

                foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily is AddressFamily.InterNetwork && 
                        !IPAddress.IsLoopback(ip.Address))
                    {
                        addresses.Add(new IPAddressInfo(
                            Address: ip.Address,
                            SubnetMask: ip.IPv4Mask,
                            Gateway: gateway,
                            IsPhysical: isPhysical
                        ));
                    }
                }
            }
        }
        
        return addresses
            .OrderByDescending(a => a.Gateway is not null && !a.Gateway.Equals(IPAddress.Any))
            .ThenByDescending(a => a.IsPhysical)
            .ToList();
    }

    public record IPAddressInfo(IPAddress Address, IPAddress SubnetMask, IPAddress? Gateway, bool IsPhysical = false);
}
