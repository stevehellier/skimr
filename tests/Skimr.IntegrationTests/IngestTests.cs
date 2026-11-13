using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Runs a real refresh against each database provider. Catches provider-specific issues such as DateTime kinds or NUL on Postgres.</summary>
public abstract class IngestTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Rss = """
        <rss version="2.0"><channel><title>Provider test</title><link>https://p.example.com/</link>
        <item><title>Ünïcødé title</title><guid>p1</guid><pubDate>Wed, 11 Nov 2026 10:00:00 +0100</pubDate><description>&lt;p&gt;Hi&lt;/p&gt;</description></item>
        <item><title>Second</title><guid>p2</guid></item>
        </channel></rss>
        """;

    private async Task<(long FeedId, Guid[] Users)> RefreshAsync(string body, int times = 1)
    {
        await using var factory = app.WithServices(s => s.AddSingleton<IOutboundHttp>(new StaticHttp(body)));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        Guid[] users = [await TestData.AddUserAsync(db, Ct), await TestData.AddUserAsync(db, Ct)];
        var feed = new Feed { Url = $"https://p.example.com/{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        db.Subscriptions.AddRange(users.Select(u => new Subscription { UserId = u, FeedId = feed.Id, CreatedAt = DateTime.UtcNow }));
        await db.SaveChangesAsync(Ct);

        for (var i = 0; i < times; i++)
        {
            await scope.ServiceProvider.GetRequiredService<IFeedRefresher>().RefreshAsync(feed.Id, Ct);
        }

        return (feed.Id, users);
    }

    private async Task<T> QueryAsync<T>(Func<SkimrDbContext, Task<T>> query)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<SkimrDbContext>());
    }

    [Fact]
    public async Task Refresh_stores_articles_and_fans_out_on_this_provider()
    {
        var (feedId, users) = await RefreshAsync(Rss, times: 2);

        var articles = await QueryAsync(db => db.Articles.Where(a => a.FeedId == feedId).OrderBy(a => a.Guid).ToListAsync(Ct));
        articles.Should().HaveCount(2);
        articles[0].Title.Should().Be("Ünïcødé title");
        articles[0].PublishedAt.Should().Be(new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc));
        articles[0].PublishedAt.Kind.Should().Be(DateTimeKind.Utc);

        var unread = await QueryAsync(db => db.UserArticles
            .Where(u => u.FeedId == feedId && !u.IsRead)
            .GroupBy(u => u.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(Ct));
        unread.Should().BeEquivalentTo(users.Select(u => new { Key = u, Count = 2 }));

        var feed = await QueryAsync(db => db.Feeds.SingleAsync(f => f.Id == feedId, Ct));
        feed.Title.Should().Be("Provider test");
        feed.LastSuccessAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("""<rss version="2.0"><channel><title>N&#0;ul feed</title><item><title>a&#0;b&#x1;c</title><guid>n&#0;1</guid><author>x&#0;y</author></item></channel></rss>""")]
    [InlineData("""{"version":"https://jsonfeed.org/version/1.1","title":"N\u0000ul feed","items":[{"id":"n\u00001","title":"a\u0000b\u0001c","authors":[{"name":"x\u0000y"}]}]}""")]
    public async Task Nul_and_control_characters_are_stripped_before_storing(string body)
    {
        var (feedId, _) = await RefreshAsync(body);

        var feed = await QueryAsync(db => db.Feeds.SingleAsync(f => f.Id == feedId, Ct));
        feed.ConsecutiveFailures.Should().Be(0, feed.LastError);
        feed.Title.Should().Be("Nul feed");
        var article = await QueryAsync(db => db.Articles.SingleAsync(a => a.FeedId == feedId, Ct));
        article.Title.Should().Be("abc");
        article.Guid.Should().Be("n1");
    }

    private sealed class StaticHttp(string body) : IOutboundHttp
    {
        public Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new FetchResponse(HttpStatusCode.OK, request.Uri, Encoding.UTF8.GetBytes(body), "application/rss+xml", null, null, null, null, null, null));

        public Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

public sealed class SqliteIngestTests(SqliteAppFixture app) : IngestTests(app), IClassFixture<SqliteAppFixture>;

public sealed class PostgresIngestTests(PostgresAppFixture app) : IngestTests(app), IClassFixture<PostgresAppFixture>;
