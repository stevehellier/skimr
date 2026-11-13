using System.Net;
using Skimr.Infrastructure.Fetching;

namespace Skimr.UnitTests.Fetching;

public sealed class IpPolicyTests
{
    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    public void Public_addresses_are_allowed(string ip)
    {
        IpPolicy.IsAllowed(IPAddress.Parse(ip), allowPrivate: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:0101::1")]
    public void Private_addresses_are_blocked_by_default_and_allowed_by_the_flag(string ip)
    {
        var address = IPAddress.Parse(ip);

        IpPolicy.IsAllowed(address, allowPrivate: false).Should().BeFalse();
        IpPolicy.IsAllowed(address, allowPrivate: true).Should().BeTrue();
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("100.100.100.200")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("ff02::1")]
    public void Metadata_and_non_unicast_addresses_are_always_blocked(string ip)
    {
        var address = IPAddress.Parse(ip);

        IpPolicy.IsAllowed(address, allowPrivate: false).Should().BeFalse();
        IpPolicy.IsAllowed(address, allowPrivate: true).Should().BeFalse();
    }
}
