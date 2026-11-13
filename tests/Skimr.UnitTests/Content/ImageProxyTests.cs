using System.Text;
using Skimr.Infrastructure.Content;

namespace Skimr.UnitTests.Content;

public sealed class ImageProxyTests
{
    private static ImageProxy Proxy(bool enabled = true, byte key = 1) =>
        new(new ContentOptions { ImageProxyEnabled = enabled }, new ImageProxyKey(Enumerable.Repeat(key, 32).ToArray()));

    [Fact]
    public void Signed_urls_round_trip_and_tampering_fails()
    {
        var proxy = Proxy();
        var url = proxy.Url("http://images.example.com/a b.png?x=1")!;
        url.Should().StartWith(ImageProxy.RoutePrefix);
        var parts = url[ImageProxy.RoutePrefix.Length..].Split('/');

        proxy.Verify(parts[0], parts[1]).Should().Be("http://images.example.com/a%20b.png?x=1");
        proxy.Verify(parts[0], parts[1] + "A").Should().BeNull("payload changed");
        proxy.Verify("A" + parts[0][1..], parts[1]).Should().BeNull("signature changed");
        Proxy(key: 2).Verify(parts[0], parts[1]).Should().BeNull("signed with another installation's key");
        proxy.Verify("x", "%%%not-base64").Should().BeNull();
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("img/local.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public void Only_absolute_http_urls_are_proxied(string? url)
    {
        Proxy().Url(url).Should().Be(url);
    }

    [Fact]
    public void Html_images_are_rewritten_and_nothing_else_changes()
    {
        var proxy = Proxy();

        var html = proxy.RewriteHtml("<p>Hi <a href=\"https://x.example/\">link</a><img src=\"https://x.example/a.png\" alt=\"A\"><img src=\"http://y.example/b.gif\"></p>");

        html.Should().Contain("<a href=\"https://x.example/\">link</a>");
        html.Should().NotContain("x.example/a.png").And.NotContain("y.example/b.gif");
        html.Should().Contain("src=\"img/").And.Contain("alt=\"A\"");
    }

    [Fact]
    public void Disabled_proxy_leaves_everything_alone()
    {
        var proxy = Proxy(enabled: false);
        const string html = "<img src=\"https://x.example/a.png\">";

        proxy.RewriteHtml(html).Should().Be(html);
        proxy.Url("https://x.example/a.png").Should().Be("https://x.example/a.png");
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 }, "image/x-icon")]
    public void Image_formats_are_recognised_by_their_bytes(byte[] bytes, string mime)
    {
        ImageBytes.Sniff(bytes).Should().Be(mime);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/svg+xml")]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- logo -->\n<svg></svg>", "image/svg+xml")]
    [InlineData("<!DOCTYPE html><html><body>Not found</body></html>", null)]
    [InlineData("{\"error\":\"nope\"}", null)]
    [InlineData("", null)]
    public void Text_responses_are_only_accepted_as_svg(string text, string? mime)
    {
        ImageBytes.Sniff(Encoding.UTF8.GetBytes(text)).Should().Be(mime);
    }
}
