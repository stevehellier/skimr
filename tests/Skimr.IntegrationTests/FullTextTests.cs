using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

public abstract class FullTextTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Page(string marker) => $$"""
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>Page</title><script>alert(1)</script></head>
        <body><nav>Home | About | Contact</nav>
        <article><h1>The headline</h1>
        <p>{{marker}} is the first paragraph of a long article body that SmartReader should keep because it is the main content of the page.</p>
        <p>Second paragraph with more words so the readability heuristics are confident this is an article and not navigation.</p>
        <p>Third paragraph, also substantial, with <a href="/relative/link" onclick="steal()">a link</a> and an image <img src="pic.jpg" onerror="x()">.</p>
        <p>Fourth paragraph to make the content long enough for extraction to succeed reliably on any reasonable heuristic.</p>
        {{string.Concat(Enumerable.Range(5, 8).Select(i => $"<p>Paragraph {i} keeps going with ordinary prose, commas, and sentences, the way a real blog post or news story would read from start to finish.</p>"))}}
        </article><footer>Copyright</footer></body></html>
        """;

    private sealed class PageHttp(Func<FetchRequest, FetchResponse> respond) : IOutboundHttp
    {
        public int Calls { get; private set; }

        public Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }

        public Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static FetchResponse Html(FetchRequest request, byte[] body, string type = "text/html", string? charset = null, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status, request.Uri, body, type, null, null, null, null, null, null, charset);

    private async Task<(Guid User, long ArticleId)> SeedAsync(IServiceProvider services, string url)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        var user = await TestData.AddUserAsync(db, Ct);
        var feed = new Feed { Url = $"https://fulltext.example.com/{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        var article = new Article
        {
            FeedId = feed.Id, DedupKey = Guid.NewGuid().ToString("N"), Title = "Short item", Url = url, ContentHtml = "<p>Teaser</p>",
            SearchText = "Teaser", ContentHash = "h", PublishedAt = DateTime.UtcNow, FetchedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        };
        db.Articles.Add(article);
        await db.SaveChangesAsync(Ct);
        db.Subscriptions.Add(new Subscription { UserId = user, FeedId = feed.Id, CreatedAt = DateTime.UtcNow });
        db.UserArticles.Add(new UserArticle { UserId = user, ArticleId = article.Id, FeedId = feed.Id, PublishedAt = article.PublishedAt });
        await db.SaveChangesAsync(Ct);
        return (user, article.Id);
    }

    [Fact]
    public async Task Full_text_is_extracted_sanitised_cached_and_searchable()
    {
        var marker = "ft" + Guid.NewGuid().ToString("N")[..10];
        var http = new PageHttp(r => Html(r, Encoding.UTF8.GetBytes(Page(marker))));
        await using var app = fixture.WithServices(s => s.AddSingleton<IOutboundHttp>(http));
        var (user, articleId) = await SeedAsync(app.Services, "https://site.example.com/posts/1");
        var fullText = app.Services.GetRequiredService<FullTextService>();

        var result = await fullText.GetAsync(user, articleId, Ct);

        result.Error.Should().BeNull();
        result.Html.Should().Contain(marker).And.Contain("Second paragraph")
            .And.NotContain("<script").And.NotContain("onclick").And.NotContain("onerror").And.NotContain("Copyright");
        result.Html.Should().Contain("href=\"https://site.example.com/relative/link\"", "relative links resolve against the page");

        (await fullText.GetAsync(user, articleId, Ct)).Html.Should().Be(result.Html);
        http.Calls.Should().Be(1, "the result is cached on the article");

        var found = await app.Services.GetRequiredService<ReadingService>().SearchAsync(user, new SearchQuery(marker), null, Ct);
        found.Items.Should().ContainSingle("search now covers the full text, not just the teaser");
    }

    [Fact]
    public async Task Another_users_article_is_not_fetched()
    {
        var http = new PageHttp(r => Html(r, Encoding.UTF8.GetBytes(Page("x"))));
        await using var app = fixture.WithServices(s => s.AddSingleton<IOutboundHttp>(http));
        var (_, articleId) = await SeedAsync(app.Services, "https://site.example.com/a");
        Guid stranger;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            stranger = await TestData.AddUserAsync(scope.ServiceProvider.GetRequiredService<SkimrDbContext>(), Ct);
        }

        var result = await app.Services.GetRequiredService<FullTextService>().GetAsync(stranger, articleId, Ct);

        result.Error.Should().Be("Article not found.");
        http.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "text/html", "HTTP 404")]
    [InlineData(HttpStatusCode.OK, "application/pdf", "isn't a web page")]
    [InlineData(HttpStatusCode.OK, "text/html", "Couldn't find the article text")]
    public async Task Failures_are_reported_not_thrown(HttpStatusCode status, string type, string message)
    {
        var http = new PageHttp(r => Html(r, Encoding.UTF8.GetBytes("<html><body></body></html>"), type, status: status));
        await using var app = fixture.WithServices(s => s.AddSingleton<IOutboundHttp>(http));
        var (user, articleId) = await SeedAsync(app.Services, "https://site.example.com/b");

        var result = await app.Services.GetRequiredService<FullTextService>().GetAsync(user, articleId, Ct);

        result.Html.Should().BeNull();
        result.Error.Should().Contain(message);
    }

    [Fact]
    public async Task Blocked_addresses_report_the_guard_message()
    {
        var http = new PageHttp(_ => throw new FetchException("Blocked: site resolves to 10.0.0.1, a private or reserved address."));
        await using var app = fixture.WithServices(s => s.AddSingleton<IOutboundHttp>(http));
        var (user, articleId) = await SeedAsync(app.Services, "https://site.example.com/c");

        (await app.Services.GetRequiredService<FullTextService>().GetAsync(user, articleId, Ct)).Error.Should().StartWith("Blocked");
    }

    [Fact]
    public async Task Windows_1252_pages_decode_from_meta_charset()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var body = Encoding.GetEncoding(1252).GetBytes(Page("Café crème naïve").Replace("utf-8", "windows-1252", StringComparison.Ordinal));
        var http = new PageHttp(r => Html(r, body));
        await using var app = fixture.WithServices(s => s.AddSingleton<IOutboundHttp>(http));
        var (user, articleId) = await SeedAsync(app.Services, "https://site.example.com/d");

        (await app.Services.GetRequiredService<FullTextService>().GetAsync(user, articleId, Ct)).Html.Should().Contain("Café crème naïve");
    }
}

public sealed class SqliteFullTextTests(SqliteAppFixture fixture) : FullTextTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresFullTextTests(PostgresAppFixture fixture) : FullTextTests(fixture), IClassFixture<PostgresAppFixture>;
