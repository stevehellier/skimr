using System.Net;
using System.Net.Sockets;

namespace Skimr.Infrastructure.Fetching;

/// <summary>Which addresses outbound requests may connect to (SSRF guard).</summary>
internal static class IpPolicy
{
    // Never reachable, whatever AllowPrivateNetworkFeeds says: cloud metadata services and
    // addresses that aren't real unicast destinations.
    private static readonly IPNetwork[] AlwaysBlocked =
    [
        IPNetwork.Parse("169.254.169.254/32"), // AWS, GCP, Azure, OpenStack metadata
        IPNetwork.Parse("169.254.170.2/32"),   // AWS ECS task metadata
        IPNetwork.Parse("100.100.100.200/32"), // Alibaba metadata
        IPNetwork.Parse("fd00:ec2::254/128"),  // AWS IPv6 metadata
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("2001:db8::/32"),
    ];

    // Blocked unless AllowPrivateNetworkFeeds is on.
    private static readonly IPNetwork[] Private =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");

    public static bool IsAllowed(IPAddress address, bool allowPrivate)
    {
        // IPv6 forms that carry an IPv4 address are judged by that address.
        var embedded = EmbeddedIPv4(address);
        if (embedded is not null && !IsAllowed(embedded, allowPrivate))
        {
            return false;
        }

        if (AlwaysBlocked.Any(n => n.Contains(address)))
        {
            return false;
        }

        return allowPrivate || !Private.Any(n => n.Contains(address));
    }

    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (Nat64.Contains(address))
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        if (SixToFour.Contains(address))
        {
            return new IPAddress(bytes.AsSpan(2, 4));
        }

        // IPv4-compatible (::a.b.c.d, deprecated but still parsed).
        return bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && !address.Equals(IPAddress.IPv6Loopback) && !address.Equals(IPAddress.IPv6Any)
            ? new IPAddress(bytes.AsSpan(12, 4))
            : null;
    }
}
