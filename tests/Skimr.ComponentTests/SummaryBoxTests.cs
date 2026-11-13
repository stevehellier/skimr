using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Skimr.Domain;
using Skimr.Infrastructure.Ai;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Web.Components.Reader;

namespace Skimr.ComponentTests;

/// <summary>The Summarise button: hidden without consent, and model output shown as text only.</summary>
public sealed class SummaryBoxTests : BunitContext
{
    private readonly SqliteConnection _keepAlive;
    private readonly Guid _userId = Guid.NewGuid();
    private long _articleId;

    private sealed class HostileSummariser : ISummariser
    {
        public Task<string> SummariseAsync(AiArticle article, CancellationToken cancellationToken) =>
            Task.FromResult("<img src=x onerror=alert(1)><b>Bold</b> claim");

        public Task<IReadOnlySet<int>> MatchAsync(string rule, IReadOnlyList<AiArticle> articles, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }

    public SummaryBoxTests()
    {
        var connectionString = $"Data Source=summary-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        Services.AddDbContextFactory<SkimrDbContext>(o => o.UseSqlite(connectionString));
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton(new AiOptions { Provider = AiProvider.Anthropic, Model = "test-model" });
        Services.AddSingleton<ISummariser>(new HostileSummariser());
        Services.AddScoped<AiService>();
    }

    protected override void Dispose(bool disposing)
    {
        _keepAlive.Dispose();
        base.Dispose(disposing);
    }

    private async Task SeedAsync(bool aiEnabled)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<SkimrDbContext>>().CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new SkimrUser { Id = _userId, UserName = "alice", NormalizedUserName = "ALICE", SecurityStamp = "s", CreatedAt = DateTime.UtcNow });
        db.UserPreferences.Add(new UserPreferences { UserId = _userId, AiEnabled = aiEnabled });
        var feed = new Feed { Url = "https://example.test/feed", Title = "Feed", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync();
        var article = new Article { FeedId = feed.Id, DedupKey = "a", Title = "Story", SearchText = "Body", ContentHash = "h", PublishedAt = DateTime.UtcNow, FetchedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        db.UserArticles.Add(new UserArticle { UserId = _userId, ArticleId = article.Id, FeedId = feed.Id, PublishedAt = article.PublishedAt });
        await db.SaveChangesAsync();
        _articleId = article.Id;
    }

    [Fact]
    public async Task Model_output_is_shown_as_text_never_as_markup()
    {
        await SeedAsync(aiEnabled: true);
        var box = Render<SummaryBox>(p => p.Add(x => x.UserId, _userId).Add(x => x.ArticleId, _articleId));

        await box.Find("#summarise").ClickAsync(new());

        box.WaitForAssertion(() => box.FindAll("[data-testid=summary]").Should().ContainSingle());
        var summary = box.Find("[data-testid=summary]");
        summary.TextContent.Should().Be("<img src=x onerror=alert(1)><b>Bold</b> claim");
        summary.Children.Should().BeEmpty("no element from the model's output is rendered");
    }

    [Fact]
    public async Task Without_the_users_consent_there_is_no_button()
    {
        await SeedAsync(aiEnabled: false);

        var box = Render<SummaryBox>(p => p.Add(x => x.UserId, _userId).Add(x => x.ArticleId, _articleId));

        box.FindAll("#summarise").Should().BeEmpty();
        box.Markup.Trim().Should().BeEmpty();
    }
}
