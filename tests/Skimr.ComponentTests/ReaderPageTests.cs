using System.Net;
using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Skimr.Domain;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.Web.Components.Reader;

namespace Skimr.ComponentTests;

/// <summary>The whole reader page against real services and an in-memory SQLite database.</summary>
public sealed class ReaderPageTests : BunitContext
{
    private readonly SqliteConnection _keepAlive;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly List<long> _articleIds = [];
    private long _subscriptionId;
    private readonly BunitAuthorizationContext _auth;

    public ReaderPageTests()
    {
        var connectionString = $"Data Source=reader-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        Services.AddDbContextFactory<SkimrDbContext>(o => o.UseSqlite(connectionString));
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(new FetchOptions { Enabled = false });
        Services.AddSingleton<IOutboundHttp>(new NoHttp());
        Services.AddSingleton<FeedDiscovery>();
        Services.AddSingleton<FeedScheduler>();
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IArticleSearch>(new Skimr.Infrastructure.Sqlite.SqliteArticleSearch());
        Services.AddScoped<ReadingService>();
        Services.AddScoped<LibraryService>();
        Services.AddScoped<BoardService>();
        Services.AddSingleton(new Skimr.Infrastructure.Content.ContentOptions());
        Services.AddSingleton<Skimr.Infrastructure.Content.ContentSanitizer>();
        Services.AddScoped<FullTextService>();
        Services.AddSingleton(new Skimr.Infrastructure.Content.ImageProxyKey(new byte[32]));
        Services.AddSingleton<Skimr.Infrastructure.Content.ImageProxy>();
        // AI off, as on a default install: the summary box and AI feed links must stay hidden.
        Services.AddSingleton(new Skimr.Infrastructure.Ai.AiOptions());
        Services.AddScoped<Skimr.Infrastructure.Ai.AiService>();

        _auth = AddAuthorization();
        _auth.SetAuthorized("alice").SetClaims(new Claim(ClaimTypes.NameIdentifier, _userId.ToString()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/reader.js").Mode = JSRuntimeMode.Loose;

        SeedAsync().GetAwaiter().GetResult();
    }

    protected override void Dispose(bool disposing)
    {
        _keepAlive.Dispose();
        base.Dispose(disposing);
    }

    private async Task SeedAsync()
    {
        var factory = Services.GetRequiredService<IDbContextFactory<SkimrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new SkimrUser { Id = _userId, UserName = "alice", NormalizedUserName = "ALICE", SecurityStamp = "s", CreatedAt = DateTime.UtcNow });
            var feed = new Feed { Url = "https://bbc.example/feed", Title = "BBC", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
            db.Feeds.Add(feed);
            await db.SaveChangesAsync();
            for (var i = 0; i < 3; i++)
            {
                var article = new Article
                {
                    FeedId = feed.Id,
                    DedupKey = $"k{i}",
                    Title = $"Story {i}",
                    ContentHash = "h",
                    ContentHtml = $"<p>Body of story {i}</p>",
                    Url = $"https://bbc.example/{i}",
                    PublishedAt = DateTime.UtcNow.AddHours(-i - 1),
                    FetchedAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow,
                };
                db.Articles.Add(article);
                await db.SaveChangesAsync();
                _articleIds.Add(article.Id);
            }
        }

        var result = await Services.GetRequiredService<LibraryService>().SubscribeAsync(_userId, "https://bbc.example/feed", [], CancellationToken.None);
        _subscriptionId = result.Id!.Value;
    }

    private IRenderedComponent<Reader> RenderReader()
    {
        var reader = Render<Reader>();
        reader.WaitForElement("ul.article-list");
        return reader;
    }

    private static string AllCount(IRenderedComponent<Reader> reader) => reader.Find("a.nav-link[href=''] .count .n").TextContent;

    [Fact]
    public void Shows_the_users_articles_newest_first_with_nav_counts()
    {
        var reader = RenderReader();

        reader.FindAll(".article-item .title").Select(t => t.TextContent).Should().Equal("Story 0", "Story 1", "Story 2");
        AllCount(reader).Should().Be("3");
        reader.Find("h1").TextContent.Should().Be("All");
    }

    [Fact]
    public async Task An_empty_all_view_fills_in_when_the_first_fetch_lands()
    {
        // After an OPML import the user lands on All before any source has been fetched.
        var factory = Services.GetRequiredService<IDbContextFactory<SkimrDbContext>>();
        List<UserArticle> rows;
        await using (var db = await factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken))
        {
            rows = await db.UserArticles.AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken);
            await db.UserArticles.ExecuteDeleteAsync(Xunit.TestContext.Current.CancellationToken);
        }

        var reader = Render<Reader>();
        reader.WaitForElement(".empty");
        reader.FindAll(".article-item").Should().BeEmpty();

        await using (var db = await factory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken))
        {
            db.UserArticles.AddRange(rows);
            await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        reader.WaitForAssertion(() => reader.FindAll(".article-item").Should().HaveCount(3), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Opening_an_article_shows_it_and_marks_it_read()
    {
        var reader = RenderReader();

        reader.FindAll("button.open")[0].Click();

        reader.WaitForAssertion(() => reader.Find(".article-pane h2").TextContent.Should().Be("Story 0"));
        reader.Find(".article-body").InnerHtml.Should().Be("<p>Body of story 0</p>");
        reader.FindAll("li.article-item")[0].ClassList.Should().Contain("read");
        reader.WaitForAssertion(() => AllCount(reader).Should().Be("2"));
    }

    [Fact]
    public async Task Keyboard_j_opens_the_next_article_and_m_and_s_toggle_its_state()
    {
        var reader = RenderReader();

        await reader.InvokeAsync(() => reader.Instance.OnShortcut("j"));
        await reader.InvokeAsync(() => reader.Instance.OnShortcut("j"));
        reader.WaitForAssertion(() => reader.Find(".article-pane h2").TextContent.Should().Be("Story 1"));

        await reader.InvokeAsync(() => reader.Instance.OnShortcut("m"));
        await reader.InvokeAsync(() => reader.Instance.OnShortcut("s"));

        reader.WaitForAssertion(() =>
        {
            var item = reader.FindAll("li.article-item")[1];
            item.ClassList.Should().Contain("unread", "m toggled it back to unread");
            item.QuerySelectorAll(".item-actions button")[0].GetAttribute("aria-pressed").Should().Be("true", "s saved it");
            reader.Find("a.nav-link[href='saved'] .count .n").TextContent.Should().Be("1");
        });
    }

    [Fact]
    public async Task Scrolling_past_articles_marks_them_read()
    {
        await Services.GetRequiredService<ReadingService>().SetMarkReadModeAsync(_userId, MarkReadMode.OnScroll, CancellationToken.None);
        var reader = RenderReader();

        await reader.InvokeAsync(() => reader.Instance.OnScrolledPast([_articleIds[0], _articleIds[1]]));

        reader.WaitForAssertion(() => AllCount(reader).Should().Be("1"));
        JSInterop.VerifyInvoke("watchScroll").Arguments.Should().Equal(true);
    }

    [Fact]
    public void Layout_choice_is_remembered_for_the_view()
    {
        var reader = RenderReader();

        reader.FindAll(".segmented button").Single(b => b.TextContent == "Cards").Click();

        reader.WaitForAssertion(() => reader.Find("ul.article-list").ClassList.Should().Contain("layout-cards"));
        var again = Render<Reader>();
        again.WaitForAssertion(() => again.Find("ul.article-list").ClassList.Should().Contain("layout-cards"));
    }

    [Fact]
    public void Another_users_articles_never_appear()
    {
        // A second user subscribed to nothing sees an empty reader, not Alice's articles.
        var stranger = Guid.NewGuid();
        using (var db = Services.GetRequiredService<IDbContextFactory<SkimrDbContext>>().CreateDbContext())
        {
            db.Users.Add(new SkimrUser { Id = stranger, UserName = "bob", NormalizedUserName = "BOB", SecurityStamp = "s", CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        _auth.SetAuthorized("bob").SetClaims(new Claim(ClaimTypes.NameIdentifier, stranger.ToString()));
        var reader = Render<Reader>();

        reader.WaitForAssertion(() => reader.Find(".empty").TextContent.Should().Contain("No sources yet"));
        reader.FindAll(".article-item").Should().BeEmpty();
        _ = _subscriptionId;
    }

    private sealed class NoHttp : IOutboundHttp
    {
        public Task<FetchResponse> GetAsync(FetchRequest request, CancellationToken cancellationToken) =>
            throw new FetchException("No network in component tests.");

        public Task<FetchResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken) =>
            throw new FetchException("No network in component tests.");
    }
}
