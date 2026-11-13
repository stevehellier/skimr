using System.Net;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;
using Skimr.UnitTests.Fetching;

namespace Skimr.UnitTests.Feeds;

public sealed class FeedDiscoveryTests
{
    private const string Rss = "<rss version=\"2.0\"><channel><title>Found it</title><link>https://site.example.com/</link></channel></rss>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FeedDiscovery Discovery, FakeOutboundHttp Http) Create(Dictionary<string, string> pages)
    {
        var http = new FakeOutboundHttp(request => pages.TryGetValue(request.Uri.AbsoluteUri, out var body)
            ? FakeOutboundHttp.Ok(request.Uri, body)
            : FakeOutboundHttp.Response(HttpStatusCode.NotFound, request.Uri, []));
        return (new FeedDiscovery(http), http);
    }

    [Fact]
    public async Task A_feed_url_is_returned_directly()
    {
        var (discovery, http) = Create(new() { ["https://site.example.com/feed.xml"] = Rss });

        var found = await discovery.DiscoverAsync("https://site.example.com/feed.xml", Ct);

        found.Should().Equal(new DiscoveredFeed("https://site.example.com/feed.xml", "Found it"));
        http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Alternate_links_in_a_page_are_followed()
    {
        var page = """
            <html><head>
            <link rel="alternate" type="application/rss+xml" href="/blog/rss">
            <link rel="alternate stylesheet" type="text/css" href="/x.css">
            <link rel="alternate" type="application/atom+xml" href="https://other.example.com/missing.atom">
            </head></html>
            """;
        var (discovery, _) = Create(new()
        {
            ["https://site.example.com/"] = page,
            ["https://site.example.com/blog/rss"] = Rss,
        });

        var found = await discovery.DiscoverAsync("site.example.com", Ct);

        found.Should().Equal(new DiscoveredFeed("https://site.example.com/blog/rss", "Found it"));
    }

    [Fact]
    public async Task Common_paths_are_probed_when_the_page_has_no_links()
    {
        var (discovery, http) = Create(new()
        {
            ["https://site.example.com/"] = "<html><head><title>No feeds</title></head></html>",
            ["https://site.example.com/index.xml"] = Rss,
        });

        var found = await discovery.DiscoverAsync("https://site.example.com/", Ct);

        found.Should().Equal(new DiscoveredFeed("https://site.example.com/index.xml", "Found it"));
        http.Requests.Select(r => r.Uri.AbsolutePath).Should().Contain(["/feed", "/rss", "/atom.xml", "/index.xml"]);
    }

    [Fact]
    public async Task Nothing_found_returns_empty()
    {
        var (discovery, _) = Create(new() { ["https://site.example.com/"] = "<html></html>" });

        (await discovery.DiscoverAsync("https://site.example.com/", Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("feed://example.com/rss", "https://example.com/rss")]
    [InlineData("  http://example.com/a  ", "http://example.com/a")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("", null)]
    public void Input_is_normalised(string input, string? expected)
    {
        FeedDiscovery.Normalise(input)?.AbsoluteUri.Should().Be(expected);
        if (expected is null)
        {
            FeedDiscovery.Normalise(input).Should().BeNull();
        }
    }

    [Fact]
    public async Task Unusable_input_throws_a_readable_error()
    {
        var (discovery, _) = Create([]);

        var act = () => discovery.DiscoverAsync("javascript:alert(1)", Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("not a web address");
    }
}
