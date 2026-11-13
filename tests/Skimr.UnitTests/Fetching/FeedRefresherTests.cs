using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Skimr.Domain;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Fetching;

namespace Skimr.UnitTests.Fetching;

public sealed class FeedRefresherTests : IDisposable
{
    private static readonly Uri FeedUri = new("https://blog.example.com/feed/");
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private readonly TestDb _db = new();
    private readonly FakeTime _time = new(new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeOutboundHttp _http = new(_ => FakeOutboundHttp.Ok(FeedUri, Rss(Item("1", "One"), Item("2", "Two")), etag: "\"e1\""));
    private readonly long _feedId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FeedRefresherTests()
    {
        using var db = _db.Create();
        var feed = new Feed { Url = FeedUri.AbsoluteUri, CreatedAt = _time.UtcNow, NextFetchAt = _time.UtcNow };
        db.Feeds.Add(feed);
        db.SaveChanges();
        db.Subscriptions.AddRange(
            new Subscription { UserId = Alice, FeedId = feed.Id, CreatedAt = _time.UtcNow },
            new Subscription { UserId = Bob, FeedId = feed.Id, CreatedAt = _time.UtcNow, RefreshMinutes = 15 });
        db.SaveChanges();
        _feedId = feed.Id;
    }

    public void Dispose() => _db.Dispose();

    private static string Item(string guid, string title, string body = "<p>Body</p>", string pubDate = "Wed, 11 Nov 2026 10:00:00 GMT") =>
        $"<item><title>{title}</title><guid isPermaLink=\"false\">{guid}</guid><link>https://blog.example.com/{guid}</link>" +
        $"<pubDate>{pubDate}</pubDate><description><![CDATA[{body}]]></description></item>";

    private static string Rss(params string[] items) =>
        $"<rss version=\"2.0\"><channel><title>Blog</title><link>https://blog.example.com/</link>{string.Concat(items)}</channel></rss>";

    private async Task RefreshAsync()
    {
        await using var db = _db.Create();
        var refresher = new FeedRefresher(db, _http, new ContentSanitizer(new ContentOptions()), new FetchOptions(), _time, NullLogger<FeedRefresher>.Instance);
        await refresher.RefreshAsync(_feedId, Ct);
    }

    [Fact]
    public async Task New_items_are_stored_sanitised_and_fanned_out_to_every_subscriber()
    {
        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, Rss(Item("1", "One", "<p>Hi</p><script>x()</script>")));

        await RefreshAsync();

        await using var db = _db.Create();
        var article = await db.Articles.SingleAsync(Ct);
        article.Title.Should().Be("One");
        article.ContentHtml.Should().Be("<p>Hi</p>");
        article.Snippet.Should().Be("Hi");
        article.PublishedAt.Should().Be(new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc));
        article.PublishedAt.Kind.Should().Be(DateTimeKind.Utc);

        var states = await db.UserArticles.ToListAsync(Ct);
        states.Select(s => s.UserId).Should().BeEquivalentTo([Alice, Bob]);
        states.Should().OnlyContain(s => !s.IsRead && s.FeedId == _feedId && s.PublishedAt == article.PublishedAt);

        var feed = await db.Feeds.SingleAsync(Ct);
        feed.Title.Should().Be("Blog");
        feed.SiteUrl.Should().Be("https://blog.example.com/");
        feed.LastSuccessAt.Should().Be(_time.UtcNow);
        feed.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task Refetching_the_same_items_creates_no_duplicates()
    {
        await RefreshAsync();
        _time.UtcNow = _time.UtcNow.AddHours(1);
        await RefreshAsync();

        await using var db = _db.Create();
        (await db.Articles.CountAsync(Ct)).Should().Be(2);
        (await db.UserArticles.CountAsync(Ct)).Should().Be(4);
        (await db.Articles.Select(a => a.LastSeenAt).Distinct().ToListAsync(Ct)).Should().Equal(_time.UtcNow);
    }

    [Fact]
    public async Task Edited_items_update_content_but_keep_read_state()
    {
        await RefreshAsync();
        await using (var db = _db.Create())
        {
            await db.UserArticles.Where(u => u.UserId == Alice).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsRead, true), Ct);
        }

        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, Rss(Item("1", "One (updated)", "<p>New body</p>"), Item("2", "Two")));
        await RefreshAsync();

        await using var check = _db.Create();
        var article = await check.Articles.SingleAsync(a => a.Guid == "1", Ct);
        article.Title.Should().Be("One (updated)");
        article.ContentHtml.Should().Be("<p>New body</p>");
        article.UpdatedAt.Should().NotBeNull();
        (await check.UserArticles.CountAsync(u => u.UserId == Alice && u.IsRead, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Sends_validators_and_handles_not_modified()
    {
        await RefreshAsync();
        var firstSeen = _time.UtcNow;
        _time.UtcNow = firstSeen.AddHours(1);
        _http.Respond = _ => FakeOutboundHttp.Response(HttpStatusCode.NotModified, FeedUri, []);

        await RefreshAsync();

        _http.Requests[^1].ETag.Should().Be("\"e1\"");
        await using var db = _db.Create();
        var feed = await db.Feeds.SingleAsync(Ct);
        feed.LastSuccessAt.Should().Be(_time.UtcNow);
        (await db.Articles.CountAsync(a => a.LastSeenAt == _time.UtcNow, Ct))
            .Should().Be(2, "a 304 means items from the last fetch are still in the feed");
    }

    [Fact]
    public async Task Feed_interval_is_the_shortest_subscriber_interval()
    {
        await RefreshAsync();

        await using var db = _db.Create();
        var next = (await db.Feeds.SingleAsync(Ct)).NextFetchAt - _time.UtcNow;
        next.Should().BeCloseTo(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1.5));
    }

    [Fact]
    public async Task Errors_are_recorded_and_back_off()
    {
        _http.Respond = _ => FakeOutboundHttp.Response(HttpStatusCode.InternalServerError, FeedUri, []);

        await RefreshAsync();
        await RefreshAsync();

        await using var db = _db.Create();
        var feed = await db.Feeds.SingleAsync(Ct);
        feed.ConsecutiveFailures.Should().Be(2);
        feed.LastError.Should().Be("HTTP 500 InternalServerError.");
        feed.LastErrorAt.Should().Be(_time.UtcNow);
        (feed.NextFetchAt - _time.UtcNow).Should().BeCloseTo(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task Fetch_and_parse_errors_are_recorded_not_thrown()
    {
        _http.Respond = _ => throw new FetchException("Blocked: example resolves to 10.0.0.1.");
        await RefreshAsync();
        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, "<html>not a feed</html>");
        await RefreshAsync();

        await using var db = _db.Create();
        var feed = await db.Feeds.SingleAsync(Ct);
        feed.ConsecutiveFailures.Should().Be(2);
        feed.LastError.Should().StartWith("Not a feed");
    }

    [Fact]
    public async Task Success_resets_the_failure_count()
    {
        _http.Respond = _ => FakeOutboundHttp.Response(HttpStatusCode.BadGateway, FeedUri, []);
        await RefreshAsync();
        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, Rss(Item("1", "One")));
        await RefreshAsync();

        await using var db = _db.Create();
        (await db.Feeds.SingleAsync(Ct)).ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task Items_without_guid_dedupe_by_link_then_title_and_date()
    {
        var noGuid = "<item><title>Linked</title><link>https://blog.example.com/a</link></item>" +
            "<item><title>Bare</title><pubDate>Wed, 11 Nov 2026 10:00:00 GMT</pubDate></item>";
        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, Rss(noGuid));

        await RefreshAsync();
        await RefreshAsync();

        await using var db = _db.Create();
        (await db.Articles.CountAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Future_and_missing_dates_are_set_to_now()
    {
        var items = Item("future", "Future", pubDate: "Fri, 01 Jan 2100 00:00:00 GMT") +
            "<item><title>Undated</title><guid>undated</guid></item>";
        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, Rss(items));

        await RefreshAsync();

        await using var db = _db.Create();
        (await db.Articles.Select(a => a.PublishedAt).ToListAsync(Ct)).Should().OnlyContain(d => d == _time.UtcNow);
    }

    [Fact]
    public async Task Untitled_items_get_a_title_from_their_content()
    {
        _http.Respond = _ => FakeOutboundHttp.Ok(FeedUri, Rss("<item><guid>t</guid><description>Just some words here</description></item>"));

        await RefreshAsync();

        await using var db = _db.Create();
        (await db.Articles.SingleAsync(Ct)).Title.Should().Be("Just some words here");
    }
}
