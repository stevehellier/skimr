using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;
using Skimr.UnitTests.Fetching;

namespace Skimr.IntegrationTests;

/// <summary>
/// The whole engine inside the running app, with no fakes: subscribe, the background scheduler
/// fetches over real HTTP through the SSRF-guarded client, the parser and ingest store the
/// article, and it shows up in the subscriber's list.
/// </summary>
public sealed class EndToEndFetchTests(SqliteAppFixture fixture) : IClassFixture<SqliteAppFixture>
{
    private const string Rss = """
        <rss version="2.0"><channel><title>Local Feed</title><link>http://127.0.0.1/</link>
        <item><title>Fetched for real</title><guid>e2e-1</guid><description>&lt;p&gt;Hello&lt;/p&gt;&lt;script&gt;x()&lt;/script&gt;</description></item>
        </channel></rss>
        """;

    [Fact]
    public async Task Subscribing_fetches_the_feed_in_the_background_and_the_article_appears()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/feed.xml", () => Results.Text(Rss, "application/rss+xml")));
        // Loopback is blocked by default; this test opts in like a home-lab user would.
        await using var app = fixture.CreateFreshApp(("Fetch:Enabled", "true"), ("Fetch:AllowPrivateNetworkFeeds", "true"));

        Guid userId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            userId = await TestData.AddUserAsync(scope.ServiceProvider.GetRequiredService<Skimr.Infrastructure.Persistence.SkimrDbContext>(), ct);
        }

        var library = app.Services.GetRequiredService<LibraryService>();
        var reading = app.Services.GetRequiredService<ReadingService>();
        (await library.SubscribeAsync(userId, server.Url("feed.xml").AbsoluteUri, [], ct)).Succeeded.Should().BeTrue();

        ArticlePage page;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(200, ct);
            page = await reading.GetArticlesAsync(userId, ReaderView.All, ArticleSort.Newest, true, null, ct);
        }
        while (page.Items.Count == 0 && DateTime.UtcNow < deadline);

        var item = page.Items.Should().ContainSingle().Subject;
        item.Title.Should().Be("Fetched for real");
        item.SourceTitle.Should().Be("Local Feed");
        var detail = await reading.GetArticleAsync(userId, item.ArticleId, ct);
        detail!.ContentHtml.Should().Be("<p>Hello</p>", "content is sanitised on the way in");
        (await reading.GetNavAsync(userId, ct)).AllUnread.Should().Be(1);
    }
}
