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
    private readonly double[]? _credit;
    private readonly double[]? _nodeWeights;
    private readonly double _weightSum;
    private readonly object _weightGate = new();
    private int _cursor = -1;

    /// <param name="weights">
    /// Optional per-interface weights keyed by interface name. When null/empty (or no
    /// positive weight is available) the selector keeps its original plain round-robin
    /// behaviour; otherwise it performs smooth weighted round-robin.
    /// </param>
    public NetworkInterfaceSelector(EngineOptions options, IReadOnlyDictionary<string, double>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _addresses = Discover(options);

        if (weights is { Count: > 0 } && _addresses.Count > 0)
        {
            var nodeWeights = new double[_addresses.Count];
            double sum = 0;
            for (var i = 0; i < _addresses.Count; i++)
            {
                var weight = 1.0;
                if (weights.TryGetValue(_addresses[i].InterfaceName, out var supplied) && supplied > 0)
                {
                    weight = supplied;
                }

                nodeWeights[i] = weight;
                sum += weight;
            }

            if (sum > 0)
            {
                _credit = new double[_addresses.Count];
                _nodeWeights = nodeWeights;
                _weightSum = sum;
            }
        }
    }

    public IReadOnlyList<NetworkInterfaceAddress> Available => _addresses;

    public NetworkInterfaceAddress? Next()
    {
        if (_addresses.Count == 0)
        {
            return null;
        }

        if (_credit is null || _nodeWeights is null)
        {
            // No usable weights: plain round-robin, byte-for-byte as before.
            var slot = Interlocked.Increment(ref _cursor);
            return _addresses[(int)((uint)slot % (uint)_addresses.Count)];
        }

        // Smooth weighted round-robin: every node accrues credit at its weight and the
        // highest-credit node is handed out, then docked the total so credit stays bounded.
        lock (_weightGate)
        {
            var best = 0;
            var bestCredit = double.NegativeInfinity;
            for (var i = 0; i < _addresses.Count; i++)
            {
                _credit[i] += _nodeWeights[i];
                if (_credit[i] > bestCredit)
                {
                    bestCredit = _credit[i];
                    best = i;
                }
            }

            _credit[best] -= _weightSum;
            return _addresses[best];
        }
    }

    public static IReadOnlyList<NetworkInterfaceAddress> Discover(EngineOptions options)
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
