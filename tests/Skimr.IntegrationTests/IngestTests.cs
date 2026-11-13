using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Runs a real refresh against each database provider. Catches provider-specific issues such as DateTime kinds on Postgres.</summary>
public abstract class IngestTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Rss = """
        <rss version="2.0"><channel><title>Provider test</title><link>https://p.example.com/</link>
        <item><title>Ünïcødé title</title><guid>p1</guid><pubDate>Wed, 11 Nov 2026 10:00:00 +0100</pubDate><description>&lt;p&gt;Hi&lt;/p&gt;</description></item>
        <item><title>Second</title><guid>p2</guid></item>
        </channel></rss>
        """;

    [Fact]
    public async Task Refresh_stores_articles_and_fans_out_on_this_provider()
    {
        await using var factory = app.WithServices(s => s.AddSingleton<IOutboundHttp>(new StaticHttp(Rss)));
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        long feedId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
            var feed = new Feed { Url = $"https://p.example.com/{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow };
            db.Feeds.Add(feed);
            await db.SaveChangesAsync(Ct);
            db.Subscriptions.AddRange(
                new Subscription { UserId = userA, FeedId = feed.Id, CreatedAt = DateTime.UtcNow },
                new Subscription { UserId = userB, FeedId = feed.Id, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
            feedId = feed.Id;

            await scope.ServiceProvider.GetRequiredService<IFeedRefresher>().RefreshAsync(feedId, Ct);
            await scope.ServiceProvider.GetRequiredService<IFeedRefresher>().RefreshAsync(feedId, Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
            var articles = await db.Articles.Where(a => a.FeedId == feedId).OrderBy(a => a.Guid).ToListAsync(Ct);
            articles.Should().HaveCount(2);
            articles[0].Title.Should().Be("Ünïcødé title");
            articles[0].PublishedAt.Should().Be(new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc));
            articles[0].PublishedAt.Kind.Should().Be(DateTimeKind.Utc);

            var unread = await db.UserArticles
                .Where(u => u.FeedId == feedId && !u.IsRead)
                .GroupBy(u => u.UserId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(Ct);
            unread.Should().BeEquivalentTo([new { Key = userA, Count = 2 }, new { Key = userB, Count = 2 }]);

            var feed = await db.Feeds.SingleAsync(f => f.Id == feedId, Ct);
            feed.Title.Should().Be("Provider test");
            feed.LastSuccessAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }
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
