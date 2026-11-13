using Skimr.Web.Auth;

namespace Skimr.UnitTests.Auth;

public sealed class SafeRedirectTests
{
    [Theory]
    [InlineData("/account", "/account")]
    [InlineData("/skimr/feeds?x=1", "/skimr/feeds?x=1")]
    [InlineData("/", "/")]
    [InlineData(null, "home")]
    [InlineData("", "home")]
    [InlineData("account", "home")]
    [InlineData("https://evil.example/", "home")]
    [InlineData("//evil.example/", "home")]
    [InlineData("/\\evil.example/", "home")]
    [InlineData("javascript:alert(1)", "home")]
    [InlineData("/a\nb", "home")]
    public void Only_local_paths_are_allowed(string? url, string expected)
    {
        SafeRedirect.Local(url, "home").Should().Be(expected);
    }
}
