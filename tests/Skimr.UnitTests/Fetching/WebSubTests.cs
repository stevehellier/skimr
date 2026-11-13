using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Skimr.Domain;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Fetching;

namespace Skimr.UnitTests.Fetching;

public sealed class WebSubTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new(new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeOutboundHttp _http = new(_ => throw new InvalidOperationException());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    private WebSub Create(Infrastructure.Persistence.SkimrDbContext db, string? publicUrl) =>
        new(db, _http, new ArticleIngester(db, new ContentSanitizer(new ContentOptions())),
            new FetchOptions { PublicUrl = publicUrl is null ? null : new Uri(publicUrl) }, _time, NullLogger<WebSub>.Instance);

    private Feed NewFeed(DateTime? lease = null) => new()
    {
        Id = 42,
        Url = "https://blog.example.com/feed",
        CreatedAt = _time.UtcNow,
        HubUrl = "https://hub.example.com/",
        SelfUrl = "https://blog.example.com/feed",
        WebSubLeaseExpiresAt = lease,
    };

    [Fact]
    public async Task Subscribes_with_callback_secret_and_lease()
    {
        await using var db = _db.Create();
        var feed = NewFeed();

        await Create(db, "https://reader.example.com/skimr").EnsureSubscribedAsync(feed, Ct);

        var (uri, form) = _http.Posts.Should().ContainSingle().Subject;
        uri.Should().Be(new Uri("https://hub.example.com/"));
        form["hub.mode"].Should().Be("subscribe");
        form["hub.topic"].Should().Be("https://blog.example.com/feed");
        form["hub.callback"].Should().Be("https://reader.example.com/skimr/websub/42");
        form["hub.secret"].Should().HaveLength(64).And.Be(feed.WebSubSecret);
        form["hub.lease_seconds"].Should().Be("864000");
        feed.WebSubLeaseExpiresAt.Should().Be(_time.UtcNow.AddDays(1).AddHours(1), "an unverified request is retried in an hour");
    }

    [Fact]
    public async Task Never_sends_the_secret_to_a_plain_http_hub()
    {
        await using var db = _db.Create();
        var feed = NewFeed();
        feed.HubUrl = "http://hub.example.com/";

        await Create(db, "https://reader.example.com").EnsureSubscribedAsync(feed, Ct);

        _http.Posts.Should().BeEmpty();
        feed.WebSubSecret.Should().BeNull();
    }

    [Fact]
    public async Task Does_nothing_without_a_public_url()
    {
        await using var db = _db.Create();

        await Create(db, null).EnsureSubscribedAsync(NewFeed(), Ct);

        _http.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task Renews_only_when_the_lease_is_about_to_run_out()
    {
        await using var db = _db.Create();
        var webSub = Create(db, "https://reader.example.com");

        await webSub.EnsureSubscribedAsync(NewFeed(lease: _time.UtcNow.AddDays(5)), Ct);
        _http.Posts.Should().BeEmpty();

        await webSub.EnsureSubscribedAsync(NewFeed(lease: _time.UtcNow.AddHours(12)), Ct);
        _http.Posts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    [InlineData("sha512")]
    public void Valid_signatures_match(string method)
    {
        var body = Encoding.UTF8.GetBytes("<rss/>");
        var key = Encoding.UTF8.GetBytes("secret");
        var mac = method switch
        {
            "sha1" => HMACSHA1.HashData(key, body),
            "sha256" => HMACSHA256.HashData(key, body),
            _ => HMACSHA512.HashData(key, body),
        };

        WebSub.SignatureMatches("secret", body, $"{method}={Convert.ToHexStringLower(mac)}").Should().BeTrue();
        WebSub.SignatureMatches("other", body, $"{method}={Convert.ToHexStringLower(mac)}").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256")]
    [InlineData("md5=abcd")]
    [InlineData("sha256=not-hex")]
    [InlineData("sha256=00")]
    public void Bad_signatures_do_not_match(string? header)
    {
        WebSub.SignatureMatches("secret", [1, 2, 3], header).Should().BeFalse();
    }
}
