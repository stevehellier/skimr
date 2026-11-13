using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>The image proxy endpoint, the CSP in both proxy modes, and favicons (provider-independent: SQLite).</summary>
public sealed class ProxyAndFaviconTests(SqliteAppFixture fixture) : IClassFixture<SqliteAppFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeWeb(Func<Uri, (HttpStatusCode Status, string Type, byte[] Body)> respond) : IOutboundHttp
    {
        public List<Uri> Requested { get; } = [];

        public Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken)
        {
            Requested.Add(request.Uri);
            var (status, type, body) = respond(request.Uri);
            return Task.FromResult(new FetchResponse(status, request.Uri, body, type, null, null, null, null, null, null));
        }

        public Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task<Browser> SignedInAsync(WebApplicationFactory<Program> app)
    {
        var browser = Browser.Open(app);
        var code = app.Services.GetRequiredService<SetupService>().Code!;
        (await browser.SubmitAsync("/setup", "setup", new Dictionary<string, string>
        {
            ["Input.Code"] = code,
            ["Input.UserName"] = "admin",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
            ["Input.Mode"] = "Closed",
        })).StatusCode.Should().Be(HttpStatusCode.Redirect);
        return browser;
    }

    [Fact]
    public async Task Proxy_serves_real_images_with_safe_headers_and_refuses_everything_else()
    {
        var web = new FakeWeb(uri => uri.AbsolutePath switch
        {
            "/ok.png" => (HttpStatusCode.OK, "image/png", Png),
            "/lies.png" => (HttpStatusCode.OK, "image/png", Encoding.UTF8.GetBytes("<html><script>x()</script></html>")),
            "/logo.svg" => (HttpStatusCode.OK, "image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x()</script></svg>")),
            _ => (HttpStatusCode.NotFound, "text/html", Encoding.UTF8.GetBytes("missing")),
        });
        await using var fresh = fixture.CreateFreshApp();
        await using var app = fresh.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IOutboundHttp>(web)));
        using var browser = await SignedInAsync(app);
        var proxy = app.Services.GetRequiredService<ImageProxy>();

        var ok = await browser.GetAsync("/" + proxy.Url("https://cdn.example.com/ok.png"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        ok.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await ok.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(Png);

        var lies = await browser.GetAsync("/" + proxy.Url("https://cdn.example.com/lies.png"));
        lies.StatusCode.Should().Be(HttpStatusCode.NotFound, "the bytes aren't an image, whatever the header says");
        (await lies.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();

        var svg = await browser.GetAsync("/" + proxy.Url("https://cdn.example.com/logo.svg"));
        svg.StatusCode.Should().Be(HttpStatusCode.OK);
        svg.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("sandbox");

        (await browser.GetAsync("/" + proxy.Url("https://cdn.example.com/gone.png"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var signed = proxy.Url("https://cdn.example.com/ok.png")!;
        (await browser.GetAsync("/" + signed[..^2] + "AA")).StatusCode.Should().Be(HttpStatusCode.NotFound, "tampered payload");
        web.Requested.Select(u => u.AbsolutePath).Should().NotContain(p => p.EndsWith("AA", StringComparison.Ordinal));

        using var anonymous = Browser.Open(app);
        (await anonymous.GetAsync("/" + signed)).StatusCode.Should().Be(HttpStatusCode.Redirect, "signed-in users only");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:1/x.png")]
    [InlineData("http://10.0.0.1/x.png")]
    public async Task Proxy_uses_the_ssrf_guard(string target)
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = await SignedInAsync(app);
        var url = app.Services.GetRequiredService<ImageProxy>().Url(target)!;

        var response = await browser.GetAsync("/" + url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "img-src 'self' data:;")]
    [InlineData(false, "img-src 'self' data: https:;")]
    public async Task Csp_follows_the_proxy_setting(bool enabled, string expected)
    {
        await using var app = fixture.CreateFreshApp(("ImageProxy:Enabled", enabled.ToString()));
        using var browser = Browser.Open(app);

        var response = await browser.GetAsync("/setup");

        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain(expected);
        app.Services.GetRequiredService<ImageProxy>().Url("https://x.example/a.png").Should().Be(enabled ? app.Services.GetRequiredService<ImageProxy>().Url("https://x.example/a.png") : "https://x.example/a.png");
    }

    [Fact]
    public async Task Favicons_are_found_checked_stored_once_and_served()
    {
        var web = new FakeWeb(uri => (uri.Host, uri.AbsolutePath) switch
        {
            ("site-a.example", "/") => (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes("<html><head><link rel=\"shortcut icon\" href=\"/static/fav.png\"></head></html>")),
            ("site-a.example", "/static/fav.png") => (HttpStatusCode.OK, "image/png", Png),
            ("site-b.example", "/") => (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes("<html></html>")),
            ("site-b.example", "/favicon.ico") => (HttpStatusCode.OK, "image/x-icon", Png), // same bytes as A
            ("site-c.example", "/favicon.ico") => (HttpStatusCode.OK, "image/x-icon", Encoding.UTF8.GetBytes("<html>Not found</html>")),
            _ => (HttpStatusCode.NotFound, "text/html", []),
        });
        await using var fresh = fixture.CreateFreshApp();
        await using var app = fresh.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IOutboundHttp>(web)));
        using var browser = await SignedInAsync(app);

        long[] feeds;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
            var list = new[] { "a", "b", "c" }.Select(x => new Feed { Url = $"https://site-{x}.example/feed", SiteUrl = $"https://site-{x}.example/", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) }).ToList();
            db.Feeds.AddRange(list);
            await db.SaveChangesAsync(Ct);
            feeds = list.Select(f => f.Id).ToArray();
            foreach (var id in feeds)
            {
                await scope.ServiceProvider.GetRequiredService<FaviconService>().UpdateAsync(id, Ct);
            }
        }

        await using var check = app.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<SkimrDbContext>().Feeds.Where(f => feeds.Contains(f.Id)).OrderBy(f => f.Id).ToListAsync(Ct);
        stored[0].IconId.Should().NotBeNull();
        stored[1].IconId.Should().Be(stored[0].IconId, "identical bytes are stored once");
        stored[2].IconId.Should().BeNull("an HTML page served as favicon.ico is rejected");
        stored.Should().OnlyContain(f => f.IconCheckedAt != null, "even a failure is remembered so the site isn't asked every fetch");
        (await check.ServiceProvider.GetRequiredService<SkimrDbContext>().Icons.CountAsync(Ct)).Should().Be(1);

        var icon = await browser.GetAsync($"/icons/{stored[0].IconId}");
        icon.StatusCode.Should().Be(HttpStatusCode.OK);
        icon.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await icon.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(Png);
        (await browser.GetAsync("/icons/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
