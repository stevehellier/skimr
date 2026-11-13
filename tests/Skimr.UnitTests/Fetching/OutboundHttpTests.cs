using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Skimr.Infrastructure.Fetching;

namespace Skimr.UnitTests.Fetching;

public sealed class OutboundHttpTests : IAsyncLifetime
{
    private LocalHttpServer _server = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/feed", (HttpContext ctx) =>
            {
                if (ctx.Request.Headers.IfNoneMatch == "\"v1\"")
                {
                    return Results.StatusCode(304);
                }

                ctx.Response.Headers.ETag = "\"v1\"";
                ctx.Response.Headers.LastModified = "Wed, 04 Nov 2026 10:00:00 GMT";
                ctx.Response.Headers.CacheControl = "max-age=3600";
                ctx.Response.Headers.Link = "<https://hub.example.com/>; rel=\"hub\", <https://feeds.example.com/self>; rel=\"self\"";
                return Results.Text("<rss/>", "application/rss+xml");
            });
            app.MapGet("/to-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
            app.MapGet("/to-internal", (HttpContext ctx) => Results.Redirect($"http://internal.test:{ctx.Connection.LocalPort}/feed"));
            app.MapGet("/loop", () => Results.Redirect("/loop"));
            app.MapGet("/big", () => Results.Bytes(new byte[2 * 1024 * 1024]));
            app.MapGet("/big-chunked", async (HttpContext ctx) =>
            {
                var chunk = new byte[64 * 1024];
                for (var i = 0; i < 40; i++)
                {
                    await ctx.Response.Body.WriteAsync(chunk);
                }
            });
            app.MapGet("/busy", (HttpContext ctx) =>
            {
                ctx.Response.Headers.RetryAfter = "120";
                return Results.StatusCode(503);
            });
            app.MapGet("/slow", async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return Results.Ok();
            });
        });
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    /// <summary>The local server is on loopback, so tests that need to reach it allow exactly 127.0.0.1.</summary>
    private static OutboundHttp Client(
        FetchOptions? options = null,
        SafeHttpHandler.Resolver? resolve = null,
        Func<IPAddress, bool>? isAllowed = null,
        bool useRealPolicy = false)
    {
        options ??= new FetchOptions();
        var handler = useRealPolicy
            ? SafeHttpHandler.Create(options, resolve, null)
            : SafeHttpHandler.Create(options, resolve, isAllowed ?? IPAddress.IsLoopback);
        return new OutboundHttp(new SingleClientFactory(handler), options, TimeProvider.System);
    }

    private static FetchRequest Get(Uri uri, string? etag = null) => new(uri, "*/*", etag);

    [Fact]
    public async Task Loopback_is_blocked_by_the_default_policy()
    {
        var act = () => Client(useRealPolicy: true).GetAsync(Get(_server.Url("feed")), Ct);

        (await act.Should().ThrowAsync<FetchException>())
            .Which.Message.Should().Contain("Blocked").And.Contain("AllowPrivateNetworkFeeds");
    }

    [Fact]
    public async Task Private_network_flag_allows_loopback()
    {
        var response = await Client(new FetchOptions { AllowPrivateNetworks = true }, useRealPolicy: true)
            .GetAsync(Get(_server.Url("feed")), Ct);

        response.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reads_body_and_caching_headers()
    {
        var response = await Client().GetAsync(Get(_server.Url("feed")), Ct);

        response.IsSuccess.Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(response.Body).Should().Be("<rss/>");
        response.ContentType.Should().Be("application/rss+xml");
        response.ETag.Should().Be("\"v1\"");
        response.LastModified.Should().Be("Wed, 04 Nov 2026 10:00:00 GMT");
        response.MaxAge.Should().Be(TimeSpan.FromHours(1));
        response.HubUrl.Should().Be("https://hub.example.com/");
        response.SelfUrl.Should().Be("https://feeds.example.com/self");
    }

    [Fact]
    public async Task Conditional_get_returns_not_modified()
    {
        var response = await Client().GetAsync(Get(_server.Url("feed"), etag: "\"v1\""), Ct);

        response.Status.Should().Be(HttpStatusCode.NotModified);
        response.Body.Should().BeEmpty();
    }

    [Fact]
    public async Task Redirect_to_cloud_metadata_is_blocked()
    {
        var act = () => Client().GetAsync(Get(_server.Url("to-metadata")), Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("169.254.169.254");
    }

    [Fact]
    public async Task Redirect_to_a_host_resolving_to_a_private_address_is_blocked()
    {
        var client = Client(resolve: (host, _) => Task.FromResult(host == "internal.test"
            ? new[] { IPAddress.Parse("10.0.0.5") }
            : throw new InvalidOperationException("tests must not use real DNS")));

        var act = () => client.GetAsync(Get(_server.Url("to-internal")), Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("10.0.0.5");
    }

    [Fact]
    public async Task Dns_answer_mixing_allowed_and_blocked_addresses_is_blocked()
    {
        // DNS rebinding: one allowed answer and one internal one. Any blocked answer blocks the host.
        var client = Client(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("10.0.0.5") }));

        var act = () => client.GetAsync(Get(new Uri($"http://rebind.test:{_server.Port}/feed")), Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("Blocked");
    }

    [Fact]
    public async Task Too_many_redirects_fail()
    {
        var act = () => Client(new FetchOptions { MaxRedirects = 2 }).GetAsync(Get(_server.Url("loop")), Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("Too many redirects");
    }

    [Theory]
    [InlineData("big")]
    [InlineData("big-chunked")]
    public async Task Responses_over_the_size_limit_fail(string path)
    {
        var act = () => Client(new FetchOptions { MaxResponseBytes = 1024 * 1024 }).GetAsync(Get(_server.Url(path)), Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("1 MB limit");
    }

    [Fact]
    public async Task Retry_after_is_reported()
    {
        var response = await Client().GetAsync(Get(_server.Url("busy")), Ct);

        response.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task Slow_responses_time_out()
    {
        var act = () => Client(new FetchOptions { Timeout = TimeSpan.FromSeconds(1) }).GetAsync(Get(_server.Url("slow")), Ct);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().Contain("Timed out");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/feed")]
    [InlineData("gopher://example.com/")]
    public async Task Only_http_and_https_are_fetched(string url)
    {
        var act = () => Client().GetAsync(Get(new Uri(url)), Ct);

        await act.Should().ThrowAsync<FetchException>();
    }
}
