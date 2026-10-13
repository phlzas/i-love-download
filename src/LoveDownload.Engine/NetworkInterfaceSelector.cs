using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LoveDownload.Engine;

public sealed record NetworkInterfaceAddress(string InterfaceName, string Description, IPAddress Address);

public sealed class NetworkInterfaceSelector
{
    private static readonly HashSet<NetworkInterfaceType> UsableTypes =
    [
        NetworkInterfaceType.Ethernet,
        NetworkInterfaceType.GigabitEthernet,
        NetworkInterfaceType.FastEthernetT,
        NetworkInterfaceType.FastEthernetFx,
        NetworkInterfaceType.Ethernet3Megabit,
        NetworkInterfaceType.Wireless80211,
        NetworkInterfaceType.Ppp
    ];

    private readonly IReadOnlyList<NetworkInterfaceAddress> _addresses;
    private int _cursor = -1;

    public NetworkInterfaceSelector(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _addresses = Discover(options);
    }

    public IReadOnlyList<NetworkInterfaceAddress> Available => _addresses;

    public NetworkInterfaceAddress? Next()
    {
        if (_addresses.Count == 0)
        {
            return null;
        }

        var slot = Interlocked.Increment(ref _cursor);
        return _addresses[(int)((uint)slot % (uint)_addresses.Count)];
    }

    private static IReadOnlyList<NetworkInterfaceAddress> Discover(EngineOptions options)
    {
        var results = new List<NetworkInterfaceAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !UsableTypes.Contains(nic.NetworkInterfaceType))
            {
                continue;
            }

            if (!IsAllowed(nic, options.InterfaceAllowList))
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            catch (PlatformNotSupportedException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (!IsSelectedFamily(address, options.AddressFamily) ||
                    IPAddress.IsLoopback(address) ||
                    address.IsIPv6LinkLocal ||
                    IsIPv4LinkLocal(address))
                {
                    continue;
                }

                results.Add(new NetworkInterfaceAddress(nic.Name, nic.Description, address));
            }
        }

        return results;
    }

    private static bool IsAllowed(NetworkInterface nic, IList<string> allowList)
    {
        if (allowList.Count == 0)
        {
            return true;
        }

        foreach (var entry in allowList)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (Contains(nic.Name, entry) || Contains(nic.Description, entry) || Contains(nic.Id, entry))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsSelectedFamily(IPAddress address, AddressFamily family) =>
        family == AddressFamily.Unspecified || address.AddressFamily == family;

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
