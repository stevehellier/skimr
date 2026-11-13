using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Ai;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;
using Skimr.UnitTests.Fetching;

namespace Skimr.IntegrationTests;

/// <summary>AI summaries and AI feeds (PLAN Phase 7, D12, D62, D63), with a fake provider unless stated.</summary>
public abstract class AiTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSummariser : ISummariser
    {
        private int _calls;

        public int Calls => _calls;

        public List<string> SentTitles { get; } = [];

        public Exception? Throw { get; set; }

        public Task<string> SummariseAsync(AiArticle article, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult($"<b>Summary</b> of {article.Title}");
        }

        public Task<IReadOnlySet<int>> MatchAsync(string rule, IReadOnlyList<AiArticle> articles, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            lock (SentTitles)
            {
                SentTitles.AddRange(articles.Select(a => a.Title));
            }

            if (Throw is { } ex)
            {
                throw ex;
            }

            IReadOnlySet<int> matches = Enumerable.Range(0, articles.Count).Where(i => articles[i].Title.Contains("Rust", StringComparison.Ordinal)).ToHashSet();
            return Task.FromResult(matches);
        }
    }

    /// <summary>A fresh app with AI configured, its provider replaced by <paramref name="fake"/>.</summary>
    private sealed class AiApp(SkimrFactory app, WebApplicationFactory<Program> host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public WebApplicationFactory<Program> Host => host;

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await app.DisposeAsync();
        }

        public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            await using var scope = Services.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }
    }

    private AiApp NewApp(FakeSummariser fake, int dailyCalls = 500)
    {
        var app = fixture.CreateFreshApp(
            ("Ai:Provider", "Anthropic"), ("Ai:Model", "test-model"), ("Ai:ApiKey", "test-key"),
            ("Ai:DailyCallsPerUser", dailyCalls.ToString(CultureInfo.InvariantCulture)));
        return new AiApp(app, app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ISummariser>(fake))));
    }

    private static Task<Guid> NewUserAsync(AiApp app, bool aiEnabled = true) => app.WithAsync(async sp =>
    {
        var id = await TestData.AddUserAsync(sp.GetRequiredService<SkimrDbContext>(), Ct);
        await sp.GetRequiredService<AiService>().SetEnabledAsync(id, aiEnabled, Ct);
        return id;
    });

    private static Task<long> NewFeedAsync(AiApp app) => app.WithAsync(async sp =>
    {
        var db = sp.GetRequiredService<SkimrDbContext>();
        var feed = new Feed { Url = $"https://ai-tests.example.com/{Guid.NewGuid():N}", Title = "AI feed source", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        return feed.Id;
    });

    /// <summary>
    /// Articles as ingest makes them: the article plus a row for each listed subscriber. Fetched
    /// five minutes ago, so they're past the matcher's settle window (D62).
    /// </summary>
    private static Task<List<long>> AddArticlesAsync(AiApp app, long feedId, IReadOnlyCollection<Guid> subscribers, params string[] titles) =>
        AddArticlesAsync(app, feedId, subscribers, DateTime.UtcNow.AddMinutes(-5), titles);

    private static Task<List<long>> AddArticlesAsync(AiApp app, long feedId, IReadOnlyCollection<Guid> subscribers, DateTime fetchedAt, params string[] titles) => app.WithAsync(async sp =>
    {
        var db = sp.GetRequiredService<SkimrDbContext>();
        var articles = titles.Select(t => new Article
        {
            FeedId = feedId,
            DedupKey = Guid.NewGuid().ToString("N"),
            Title = t,
            SearchText = t + " body",
            ContentHtml = "<p>body</p>",
            ContentHash = "h",
            PublishedAt = DateTime.UtcNow.AddMinutes(-1),
            FetchedAt = fetchedAt,
            LastSeenAt = DateTime.UtcNow,
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync(Ct);
        db.UserArticles.AddRange(articles.SelectMany(a => subscribers.Select(u => new UserArticle { UserId = u, ArticleId = a.Id, FeedId = feedId, PublishedAt = a.PublishedAt })));
        await db.SaveChangesAsync(Ct);
        return articles.Select(a => a.Id).ToList();
    });

    private static async Task<long> SubscribeAsync(AiApp app, Guid userId, long feedId, params long[] folders)
    {
        var url = await app.WithAsync(sp => sp.GetRequiredService<SkimrDbContext>().Feeds.Where(f => f.Id == feedId).Select(f => f.Url).SingleAsync(Ct));
        return (await app.Services.GetRequiredService<LibraryService>().SubscribeAsync(userId, url, folders, Ct)).Id!.Value;
    }

    private static AiService Ai(AiApp app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<AiService>();

    // ---- Consent --------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_sent_to_a_real_provider_until_the_user_turns_ai_on()
    {
        // The real Anthropic client, pointed at a local server that counts what reaches it. The
        // server is on loopback with AllowPrivateNetworkFeeds off: the admin's AI URL may be (D63).
        var requests = 0;
        await using var provider = await LocalHttpServer.StartAsync(app => app.MapPost("/v1/messages", () =>
        {
            Interlocked.Increment(ref requests);
            return Results.Json(new { stop_reason = "end_turn", content = new[] { new { type = "text", text = "A real summary." } } });
        }));
        await using var app = fixture.CreateFreshApp(("Ai:Provider", "Anthropic"), ("Ai:Model", "claude-opus-5-5"), ("Ai:ApiKey", "k"), ("Ai:BaseUrl", provider.Url("").AbsoluteUri));
        var ai = new AiApp(app, app);
        var userId = await NewUserAsync(ai, aiEnabled: false);
        var feedId = await NewFeedAsync(ai);
        var folder = (await ai.Services.GetRequiredService<LibraryService>().CreateFolderAsync(userId, "Watched", Ct)).Id!.Value;
        await SubscribeAsync(ai, userId, feedId, folder);
        await Ai(ai).CreateFeedAsync(userId, "Rust", "Rust news", [folder], [], Ct);
        var ids = await AddArticlesAsync(ai, feedId, [userId], "Rust 1.0 is out");

        (await Ai(ai).SummariseAsync(userId, ids[0], Ct)).Failure.Should().Be(AiFailure.NotEnabled);
        await Ai(ai).MatchPendingAsync(Ct);
        requests.Should().Be(0);

        await Ai(ai).SetEnabledAsync(userId, true, Ct);
        (await Ai(ai).SummariseAsync(userId, ids[0], Ct)).Text.Should().Be("A real summary.");
        requests.Should().Be(1);
    }

    [Fact]
    public async Task With_ai_off_on_the_server_nothing_is_registered_and_the_api_says_so()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetService<ISummariser>().Should().BeNull();
        var ai = scope.ServiceProvider.GetRequiredService<AiService>();
        ai.Available.Should().BeFalse();
        var userId = await TestData.AddUserAsync(scope.ServiceProvider.GetRequiredService<SkimrDbContext>(), Ct);
        await ai.SetEnabledAsync(userId, true, Ct);

        (await ai.SummariseAsync(userId, 1, Ct)).Failure.Should().Be(AiFailure.Off);
        (await ai.IsEnabledForAsync(userId, Ct)).Should().BeFalse("opting in means nothing without a provider");
    }

    // ---- Summaries ------------------------------------------------------------------------

    [Fact]
    public async Task Summaries_are_cached_per_article_and_count_toward_the_daily_limit()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake, dailyCalls: 2);
        var alice = await NewUserAsync(app);
        var bob = await NewUserAsync(app);
        var feedId = await NewFeedAsync(app);
        var ids = await AddArticlesAsync(app, feedId, [alice, bob], "One", "Two", "Three");

        (await Ai(app).SummariseAsync(alice, ids[0], Ct)).Text.Should().Be("<b>Summary</b> of One");
        (await Ai(app).SummariseAsync(bob, ids[0], Ct)).Text.Should().Be("<b>Summary</b> of One", "the same article, from the cache");
        (await Ai(app).SummariseAsync(alice, ids[1], Ct)).Text.Should().NotBeNull();
        var third = await Ai(app).SummariseAsync(alice, ids[2], Ct);
        third.Failure.Should().Be(AiFailure.LimitReached);
        third.Message.Should().Contain("2 AI requests");
        (await Ai(app).SummariseAsync(alice, ids[0], Ct)).Text.Should().NotBeNull("cached summaries cost nothing");
        (await Ai(app).SummariseAsync(bob, ids[2], Ct)).Text.Should().NotBeNull("the limit is per user");
        fake.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_summary_of_someone_elses_article_is_not_found_and_costs_nothing()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var alice = await NewUserAsync(app);
        var bob = await NewUserAsync(app);
        var ids = await AddArticlesAsync(app, await NewFeedAsync(app), [alice], "Private");

        (await Ai(app).SummariseAsync(bob, ids[0], Ct)).Failure.Should().Be(AiFailure.NotFound);
        fake.Calls.Should().Be(0);
    }

    // ---- AI feeds -------------------------------------------------------------------------

    [Fact]
    public async Task An_ai_feed_checks_only_new_articles_from_its_sources_and_lists_the_matches()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var userId = await NewUserAsync(app);
        var watched = await NewFeedAsync(app);
        var other = await NewFeedAsync(app);
        var library = app.Services.GetRequiredService<LibraryService>();
        var folder = (await library.CreateFolderAsync(userId, "Tech", Ct)).Id!.Value;
        await SubscribeAsync(app, userId, watched, folder);
        await SubscribeAsync(app, userId, other);
        await AddArticlesAsync(app, watched, [userId], "Rust from before the feed existed");

        var feedId = (await Ai(app).CreateFeedAsync(userId, "Rust", "Rust language news", [folder], [], Ct)).Id!.Value;
        var fresh = await AddArticlesAsync(app, watched, [userId], "Rust 2.0 released", "Gardening tips");
        await AddArticlesAsync(app, other, [userId], "Rust in an unwatched source");
        await Ai(app).MatchPendingAsync(Ct);

        fake.SentTitles.Should().BeEquivalentTo(["Rust 2.0 released", "Gardening tips"]);
        var page = await app.Services.GetRequiredService<ReadingService>().GetArticlesAsync(userId, ReaderView.AiFeed(feedId), ArticleSort.Newest, true, null, Ct);
        page.Items.Select(i => i.ArticleId).Should().Equal(fresh[0]);
        (await app.Services.GetRequiredService<ReadingService>().GetNavAsync(userId, Ct)).AiFeedList.Single().Unread.Should().Be(1);

        await Ai(app).MatchPendingAsync(Ct);
        fake.Calls.Should().Be(1, "checked articles aren't sent again");
    }

    [Fact]
    public async Task A_user_without_ai_on_has_no_articles_checked()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var userId = await NewUserAsync(app, aiEnabled: false);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        await Ai(app).CreateFeedAsync(userId, "Rust", "Rust news", [], [sub], Ct);
        await AddArticlesAsync(app, feed, [userId], "Rust news item");

        await Ai(app).MatchPendingAsync(Ct);

        fake.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unusable_answer_skips_the_batch_but_provider_trouble_retries_it()
    {
        var fake = new FakeSummariser { Throw = new AiException("Provider is down", transient: true) };
        await using var app = NewApp(fake);
        var userId = await NewUserAsync(app);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        var aiFeed = (await Ai(app).CreateFeedAsync(userId, "Rust", "Rust news", [], [sub], Ct)).Id!.Value;
        await AddArticlesAsync(app, feed, [userId], "Rust A");

        await Ai(app).MatchPendingAsync(Ct);
        (await Ai(app).ListFeedsAsync(userId, Ct)).Single().LastError.Should().Be("Provider is down");
        fake.Throw = new AiException("The model's answer wasn't a list of article numbers.");
        await Ai(app).MatchPendingAsync(Ct);
        fake.SentTitles.Should().Equal("Rust A", "Rust A");

        fake.Throw = null;
        await AddArticlesAsync(app, feed, [userId], "Rust B");
        await Ai(app).MatchPendingAsync(Ct);
        fake.SentTitles.Last().Should().Be("Rust B", "the unusable batch was skipped, not retried");
        var matched = await app.WithAsync(sp => sp.GetRequiredService<SkimrDbContext>().AiFeedMatches.Where(m => m.AiFeedId == aiFeed).CountAsync(Ct));
        matched.Should().Be(1, "only Rust B: a skipped batch never matches");
        (await Ai(app).ListFeedsAsync(userId, Ct)).Single().LastError.Should().BeNull();
    }

    [Fact]
    public async Task Matching_stops_at_the_daily_limit_and_says_why()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake, dailyCalls: 1);
        var userId = await NewUserAsync(app);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        await Ai(app).CreateFeedAsync(userId, "Rust", "Rust news", [], [sub], Ct);
        var spent = await AddArticlesAsync(app, feed, [userId], "Summarise me");
        await Ai(app).SummariseAsync(userId, spent[0], Ct);

        await AddArticlesAsync(app, feed, [userId], "Rust news item");
        await Ai(app).MatchPendingAsync(Ct);

        fake.Calls.Should().Be(1);
        (await Ai(app).ListFeedsAsync(userId, Ct)).Single().LastError.Should().Contain("limit");
    }

    [Fact]
    public async Task Articles_fetched_in_the_last_two_minutes_wait_so_the_id_cursor_never_skips_one()
    {
        // On Postgres a newer id can commit before an older one; matching only settled articles
        // means the cursor never moves past one that wasn't visible yet.
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var userId = await NewUserAsync(app);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        await Ai(app).CreateFeedAsync(userId, "Rust", "Rust news", [], [sub], Ct);
        var fresh = await AddArticlesAsync(app, feed, [userId], DateTime.UtcNow, "Rust one", "Rust two");

        await Ai(app).MatchPendingAsync(Ct);
        fake.Calls.Should().Be(0, "nothing has settled yet");

        await app.WithAsync(sp => sp.GetRequiredService<SkimrDbContext>().Articles.Where(a => fresh.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.FetchedAt, DateTime.UtcNow.AddMinutes(-3)), Ct));
        await Ai(app).MatchPendingAsync(Ct);
        fake.SentTitles.Should().Equal("Rust one", "Rust two");
    }

    [Fact]
    public async Task Provider_downtime_does_not_use_up_the_daily_limit()
    {
        var fake = new FakeSummariser { Throw = new AiException("Ollama is off", transient: true) };
        await using var app = NewApp(fake, dailyCalls: 3);
        var userId = await NewUserAsync(app);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        await Ai(app).CreateFeedAsync(userId, "Rust", "Rust news", [], [sub], Ct);
        await AddArticlesAsync(app, feed, [userId], "Rust news item");

        for (var pass = 0; pass < 5; pass++)
        {
            await Ai(app).MatchPendingAsync(Ct);
        }

        fake.Calls.Should().Be(5);
        var used = await app.WithAsync(sp => sp.GetRequiredService<SkimrDbContext>().AiUsage.Where(u => u.UserId == userId).SumAsync(u => u.Calls, Ct));
        used.Should().Be(0);
        fake.Throw = null;
        await Ai(app).MatchPendingAsync(Ct);
        (await Ai(app).ListFeedsAsync(userId, Ct)).Single().LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_summary_from_a_provider_outage_is_not_charged()
    {
        var down = new DownSummariser();
        var app = fixture.CreateFreshApp(("Ai:Provider", "Anthropic"), ("Ai:Model", "m"), ("Ai:ApiKey", "k"), ("Ai:DailyCallsPerUser", "1"));
        await using var ai = new AiApp(app, app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ISummariser>(down))));
        var userId = await NewUserAsync(ai);
        var ids = await AddArticlesAsync(ai, await NewFeedAsync(ai), [userId], "One");

        (await Ai(ai).SummariseAsync(userId, ids[0], Ct)).Failure.Should().Be(AiFailure.ProviderFailed);
        (await Ai(ai).SummariseAsync(userId, ids[0], Ct)).Failure.Should().Be(AiFailure.ProviderFailed, "not LimitReached: the outage wasn't charged");
    }

    private sealed class DownSummariser : ISummariser
    {
        public Task<string> SummariseAsync(AiArticle article, CancellationToken cancellationToken) =>
            throw new AiException("Couldn't reach the AI provider", transient: true);

        public Task<IReadOnlySet<int>> MatchAsync(string rule, IReadOnlyList<AiArticle> articles, CancellationToken cancellationToken) =>
            throw new AiException("Couldn't reach the AI provider", transient: true);
    }

    [Fact]
    public async Task A_disabled_users_ai_feeds_stop_spending()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var userId = await NewUserAsync(app);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        await Ai(app).CreateFeedAsync(userId, "Rust", "Rust news", [], [sub], Ct);
        await AddArticlesAsync(app, feed, [userId], "Rust news item");
        await app.WithAsync(async sp =>
        {
            var accounts = sp.GetRequiredService<AccountService>();
            var admin = (await accounts.CreateUserAsync("ai-admin", "a long password here", true, Ct)).UserId!.Value;
            return (await accounts.SetDisabledAsync(admin, userId, true, Ct)).Succeeded;
        });

        await Ai(app).MatchPendingAsync(Ct);

        fake.Calls.Should().Be(0);
    }

    // ---- Ownership over HTTP --------------------------------------------------------------

    [Fact]
    public async Task Another_users_ai_feeds_and_summaries_are_out_of_reach()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var alice = await NewUserAsync(app);
        var bob = await NewUserAsync(app);
        var feed = await NewFeedAsync(app);
        var folder = (await app.Services.GetRequiredService<LibraryService>().CreateFolderAsync(alice, "Alice's", Ct)).Id!.Value;
        await SubscribeAsync(app, alice, feed, folder);
        var aiFeed = (await Ai(app).CreateFeedAsync(alice, "Rust", "Rust news", [folder], [], Ct)).Id!.Value;
        var ids = await AddArticlesAsync(app, feed, [alice], "Rust for Alice");
        await Ai(app).MatchPendingAsync(Ct);
        var token = await app.WithAsync(async sp => (await sp.GetRequiredService<ApiTokenService>().CreateAsync(bob, "t", null, Ct)).PlainText);
        using var client = app.Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.PatchAsJsonAsync($"/api/v1/ai-feeds/{aiFeed}", new { rule = "pwned" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync(new Uri($"/api/v1/ai-feeds/{aiFeed}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/v1/ai-feeds", new { name = "x", rule = "y", folderIds = new[] { folder } }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync(new Uri($"/api/v1/articles/{ids[0]}/summary", UriKind.Relative), null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/articles?view=ai:{aiFeed}", Ct);
        list.GetProperty("items").GetArrayLength().Should().Be(0);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/ai-feeds", Ct)).GetArrayLength().Should().Be(0);

        (await Ai(app).ListFeedsAsync(alice, Ct)).Single().Rule.Should().Be("Rust news");
        fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_rest_api_summarises_and_manages_ai_feeds()
    {
        var fake = new FakeSummariser();
        await using var app = NewApp(fake);
        var userId = await NewUserAsync(app, aiEnabled: false);
        var feed = await NewFeedAsync(app);
        var sub = await SubscribeAsync(app, userId, feed);
        var ids = await AddArticlesAsync(app, feed, [userId], "Hello");
        var token = await app.WithAsync(async sp => (await sp.GetRequiredService<ApiTokenService>().CreateAsync(userId, "t", null, Ct)).PlainText);
        using var client = app.Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.PostAsync(new Uri($"/api/v1/articles/{ids[0]}/summary", UriKind.Relative), null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings", Ct);
        (settings.GetProperty("aiAvailable").GetBoolean(), settings.GetProperty("aiEnabled").GetBoolean()).Should().Be((true, false));
        (await client.PatchAsJsonAsync("/api/v1/settings", new { aiEnabled = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var summary = await client.PostAsync(new Uri($"/api/v1/articles/{ids[0]}/summary", UriKind.Relative), null, Ct);
        (await summary.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("summary").GetString().Should().Be("<b>Summary</b> of Hello");

        var created = await client.PostAsJsonAsync("/api/v1/ai-feeds", new { name = "Mine", rule = "anything", subscriptionIds = new[] { sub } }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();
        (await client.PatchAsJsonAsync($"/api/v1/ai-feeds/{id}", new { rule = "something else" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/ai-feeds", Ct))[0].GetProperty("rule").GetString().Should().Be("something else");
        (await client.PostAsJsonAsync("/api/v1/ai-feeds", new { name = "", rule = "x", subscriptionIds = new[] { sub } }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.DeleteAsync(new Uri($"/api/v1/ai-feeds/{id}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

public sealed class SqliteAiTests(SqliteAppFixture fixture) : AiTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresAiTests(PostgresAppFixture fixture) : AiTests(fixture), IClassFixture<PostgresAppFixture>;
