using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>The REST API over HTTP: coverage, ownership, paging and the docs page (PLAN Phase 6).</summary>
public abstract class RestApiTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Factory.Services;

    private async Task<T> DbAsync<T>(Func<SkimrDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SkimrDbContext>());
    }

    /// <summary>A user with an API token, and a client that sends it.</summary>
    private async Task<(Guid Id, ApiClient Api)> NewUserAsync(bool isAdmin = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var name = "api-" + Guid.NewGuid().ToString("N")[..8];
        var id = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync(name, "a long password here", isAdmin, Ct)).UserId!.Value;
        var token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync(id, "t", null, Ct)).PlainText;
        return (id, new ApiClient(fixture.Factory.CreateClient(), token));
    }

    /// <summary>A feed with <paramref name="count"/> articles one hour apart, newest first.</summary>
    private Task<(string Url, List<long> Ids)> NewFeedAsync(int count, string body = "<p>Body <img src=\"http://images.example.com/a.png\"></p>") => DbAsync(async db =>
    {
        var url = $"https://api-tests.example.com/{Guid.NewGuid():N}";
        var feed = new Feed { Url = url, Title = "Api feed", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        var articles = Enumerable.Range(0, count).Select(i => new Article
        {
            FeedId = feed.Id,
            DedupKey = Guid.NewGuid().ToString("N"),
            Title = $"Story {i}",
            SearchText = "apisearchword story",
            ContentHtml = body,
            ContentHash = "h",
            PublishedAt = DateTime.UtcNow.AddHours(-i).AddMinutes(-1),
            FetchedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync(Ct);
        return (url, articles.Select(a => a.Id).ToList());
    });

    private static async Task<long> SubscribeAsync(ApiClient api, string url, params long[] folderIds)
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/v1/subscriptions", new { url, folderIds });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateAsync(ApiClient api, string path, string name)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, new { name });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();
    }

    private static async Task<List<long>> AllArticleIdsAsync(ApiClient api, string query)
    {
        var ids = new List<long>();
        string? cursor = null;
        do
        {
            var page = await api.GetJsonAsync($"{query}&limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("articleId").GetInt64()));
            cursor = page.GetProperty("next").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next").GetString();
        }
        while (cursor is not null);

        return ids;
    }

    // ---- Reading -------------------------------------------------------------------------

    [Fact]
    public async Task Paging_returns_every_article_exactly_once_in_both_orders()
    {
        var (url, ids) = await NewFeedAsync(10);
        var (_, api) = await NewUserAsync();
        var sub = await SubscribeAsync(api, url);

        (await AllArticleIdsAsync(api, $"/api/v1/articles?view=source:{sub}")).Should().Equal(ids);
        (await AllArticleIdsAsync(api, $"/api/v1/articles?view=source:{sub}&sort=Oldest")).Should().Equal(ids.AsEnumerable().Reverse());
    }

    [Theory]
    [InlineData("/api/v1/articles?cursor=not-a-cursor")]
    [InlineData("/api/v1/articles?cursor=MToyOjM")]
    [InlineData("/api/v1/articles?cursor=OTk5OTk5OTk5OTk5OTk5OTk5OTk5OjE")]
    [InlineData("/api/v1/articles?view=folder:abc")]
    [InlineData("/api/v1/articles?view=nonsense")]
    [InlineData("/api/v1/search?q=")]
    [InlineData("/api/v1/search?q=x&cursor=%25%25")]
    public async Task Bad_paging_and_view_parameters_are_400_not_500(string path)
    {
        var (_, api) = await NewUserAsync();

        var response = await api.SendAsync(HttpMethod.Get, path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task The_ui_flow_works_through_the_api()
    {
        var (url, ids) = await NewFeedAsync(4);
        var (userId, api) = await NewUserAsync();
        var folder = await CreateAsync(api, "/api/v1/folders", "Tech");
        var sub = await SubscribeAsync(api, url, folder);

        // Counts and lists.
        (await api.GetJsonAsync("/api/v1/counts")).GetProperty("allUnread").GetInt32().Should().Be(4);
        (await AllArticleIdsAsync(api, $"/api/v1/articles?view=folder:{folder}&unreadOnly=true")).Should().Equal(ids);

        // Read, save, batch, mark all read with an age limit.
        (await api.SendAsync(HttpMethod.Patch, $"/api/v1/articles/{ids[0]}", new { read = true, saved = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.GetJsonAsync($"/api/v1/articles/{ids[0]}")).GetProperty("isSaved").GetBoolean().Should().BeTrue();
        (await (await api.SendAsync(HttpMethod.Patch, "/api/v1/articles", new { ids = new[] { ids[1], ids[1] }, read = true })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("changed").GetInt32().Should().Be(1);
        (await AllArticleIdsAsync(api, "/api/v1/articles?view=saved")).Should().Equal(ids[0]);
        (await (await api.SendAsync(HttpMethod.Post, "/api/v1/articles/mark-read", new { view = $"source:{sub}", olderThanDays = 3650 })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("marked").GetInt32().Should().Be(0);
        (await (await api.SendAsync(HttpMethod.Post, "/api/v1/articles/mark-read", new { view = "all" })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("marked").GetInt32().Should().Be(2);

        // Boards.
        var board = await CreateAsync(api, "/api/v1/boards", "Keep");
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/boards/{board}/articles/{ids[2]}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AllArticleIdsAsync(api, $"/api/v1/articles?view=board:{board}")).Should().Equal(ids[2]);

        // Search with a filter.
        (await AllArticleIdsAsync(api, $"/api/v1/search?q=apisearchword&source={sub}&read=Read")).Should().BeEquivalentTo(ids);

        // Source settings, per-view preferences and the mark-read mode.
        (await api.SendAsync(HttpMethod.Patch, $"/api/v1/subscriptions/{sub}", new { customTitle = "Renamed", refreshMinutes = 60, alwaysFullText = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var source = await api.GetJsonAsync($"/api/v1/subscriptions/{sub}");
        source.GetProperty("customTitle").GetString().Should().Be("Renamed");
        source.GetProperty("refreshMinutes").GetInt32().Should().Be(60);
        source.GetProperty("alwaysFullText").GetBoolean().Should().BeTrue();
        source.GetProperty("folderIds").EnumerateArray().Select(f => f.GetInt64()).Should().Equal(folder);
        (await api.SendAsync(HttpMethod.Put, $"/api/v1/views/folder:{folder}", new { layout = "Cards", sort = "Oldest", unreadOnly = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.GetJsonAsync($"/api/v1/views/folder:{folder}")).GetProperty("layout").GetString().Should().Be("Cards");
        (await api.SendAsync(HttpMethod.Patch, "/api/v1/settings", new { markReadMode = "OnScroll" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.GetJsonAsync("/api/v1/settings")).GetProperty("markReadMode").GetString().Should().Be("OnScroll");

        // OPML out.
        var opml = await api.SendAsync(HttpMethod.Get, "/api/v1/opml");
        (await opml.Content.ReadAsStringAsync(Ct)).Should().Contain(url).And.Contain("Tech");

        // Unsubscribe; the saved article stays.
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/subscriptions/{sub}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DbAsync(db => db.UserArticles.Where(u => u.UserId == userId).Select(u => u.ArticleId).ToListAsync(Ct))).Should().Contain(ids[0]);
    }

    [Fact]
    public async Task Opml_import_takes_the_raw_file_and_rejects_oversized_ones()
    {
        var (url, _) = await NewFeedAsync(1);
        var (_, api) = await NewUserAsync();
        var opml = $"""<?xml version="1.0"?><opml version="2.0"><body><outline text="News"><outline type="rss" text="A" xmlUrl="{url}"/></outline></body></opml>""";

        var response = await api.SendRawAsync(HttpMethod.Post, "/api/v1/opml", new StringContent(opml, Encoding.UTF8, "text/x-opml"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await api.GetJsonAsync("/api/v1/folders")).EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Contain("News");

        var huge = await api.SendRawAsync(HttpMethod.Post, "/api/v1/opml", new ByteArrayContent(new byte[OpmlService.MaxUploadBytes + 1]));
        huge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Article_html_from_the_api_keeps_original_image_urls()
    {
        // API clients have no cookie, so proxied /img/ urls would be broken images for them.
        var (url, ids) = await NewFeedAsync(1);
        var (_, api) = await NewUserAsync();
        await SubscribeAsync(api, url);

        var article = await api.GetJsonAsync($"/api/v1/articles/{ids[0]}");

        article.GetProperty("contentHtml").GetString().Should().Contain("http://images.example.com/a.png").And.NotContain("/img/");
    }

    // ---- Ownership -----------------------------------------------------------------------

    [Fact]
    public async Task Another_users_ids_are_404_or_change_nothing()
    {
        var (url, ids) = await NewFeedAsync(2);
        var (aliceId, alice) = await NewUserAsync();
        var folder = await CreateAsync(alice, "/api/v1/folders", "Alice's");
        var sub = await SubscribeAsync(alice, url, folder);
        var board = await CreateAsync(alice, "/api/v1/boards", "Alice's board");
        await alice.SendAsync(HttpMethod.Put, $"/api/v1/boards/{board}/articles/{ids[0]}");
        var tokenId = (await alice.GetJsonAsync("/api/v1/tokens")).EnumerateArray().First().GetProperty("id").GetInt64();
        var (_, bob) = await NewUserAsync();

        (HttpMethod Method, string Path, object? Body)[] attempts =
        [
            (HttpMethod.Get, $"/api/v1/subscriptions/{sub}", null),
            (HttpMethod.Patch, $"/api/v1/subscriptions/{sub}", new { customTitle = "pwned" }),
            (HttpMethod.Delete, $"/api/v1/subscriptions/{sub}", null),
            (HttpMethod.Post, $"/api/v1/subscriptions/{sub}/refresh", null),
            (HttpMethod.Post, $"/api/v1/subscriptions/{sub}/move", new { direction = "up" }),
            (HttpMethod.Patch, $"/api/v1/folders/{folder}", new { name = "pwned" }),
            (HttpMethod.Delete, $"/api/v1/folders/{folder}", null),
            (HttpMethod.Post, $"/api/v1/folders/{folder}/move", new { direction = "down" }),
            (HttpMethod.Get, $"/api/v1/articles/{ids[0]}", null),
            (HttpMethod.Patch, $"/api/v1/articles/{ids[0]}", new { read = true }),
            (HttpMethod.Post, $"/api/v1/articles/{ids[0]}/full-text", null),
            (HttpMethod.Patch, $"/api/v1/boards/{board}", new { name = "pwned" }),
            (HttpMethod.Delete, $"/api/v1/boards/{board}", null),
            (HttpMethod.Put, $"/api/v1/boards/{board}/articles/{ids[1]}", null),
            (HttpMethod.Delete, $"/api/v1/boards/{board}/articles/{ids[0]}", null),
            (HttpMethod.Delete, $"/api/v1/tokens/{tokenId}", null),
        ];

        foreach (var (method, path, body) in attempts)
        {
            var response = await bob.SendAsync(method, path, body);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {path}");
        }

        // Batch and view-based calls answer 200 but touch nothing of Alice's.
        (await (await bob.SendAsync(HttpMethod.Patch, "/api/v1/articles", new { ids, read = true, saved = true })).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("changed").GetInt32().Should().Be(0);
        await bob.SendAsync(HttpMethod.Post, "/api/v1/articles/mark-read", new { view = $"folder:{folder}" });
        await bob.SendAsync(HttpMethod.Post, "/api/v1/articles/mark-read", new { view = $"source:{sub}" });
        (await bob.GetJsonAsync($"/api/v1/articles?view=board:{board}")).GetProperty("items").GetArrayLength().Should().Be(0);
        (await bob.GetJsonAsync($"/api/v1/articles?view=source:{sub}")).GetProperty("items").GetArrayLength().Should().Be(0);
        (await bob.GetJsonAsync($"/api/v1/search?q=apisearchword&source={sub}")).GetProperty("items").GetArrayLength().Should().Be(0);
        // Subscribing with Alice's folder id works, but the foreign folder is ignored.
        var bobSub = await SubscribeAsync(bob, url, folder);
        (await bob.GetJsonAsync($"/api/v1/subscriptions/{bobSub}")).GetProperty("folderIds").GetArrayLength().Should().Be(0);

        var aliceState = await DbAsync(db => db.UserArticles.Where(u => u.UserId == aliceId).Select(u => new { u.IsRead, u.IsSaved }).ToListAsync(Ct));
        aliceState.Should().AllSatisfy(a => a.Should().Be(new { IsRead = false, IsSaved = false }));
        var aliceSource = await alice.GetJsonAsync($"/api/v1/subscriptions/{sub}");
        aliceSource.GetProperty("customTitle").ValueKind.Should().Be(JsonValueKind.Null);
        (await alice.GetJsonAsync($"/api/v1/articles?view=board:{board}")).GetProperty("items").GetArrayLength().Should().Be(1);
        (await alice.GetJsonAsync("/api/v1/folders")).EnumerateArray().Single().GetProperty("name").GetString().Should().Be("Alice's");
    }

    [Fact]
    public async Task Admin_endpoints_need_an_admin_token()
    {
        var (_, user) = await NewUserAsync();
        var (_, admin) = await NewUserAsync(isAdmin: true);

        (await user.SendAsync(HttpMethod.Get, "/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.SendAsync(HttpMethod.Post, "/api/v1/admin/invites", new { note = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.SendAsync(HttpMethod.Get, "/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.SendAsync(HttpMethod.Get, "/api/v1/admin/feeds")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{Guid.NewGuid()}", new { disabled = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Docs ----------------------------------------------------------------------------

    /// <summary>Every operation the UI offers, by OpenAPI operationId (PLAN §3: "everything the UI can do").</summary>
    public static readonly string[] UiOperations =
    [
        "GetMe", "ListTokens", "CreateToken", "RevokeToken",
        "DiscoverFeeds", "ListSubscriptions", "Subscribe", "GetSubscription", "UpdateSubscription", "Unsubscribe", "RefreshSubscription", "MoveSubscription",
        "ExportOpml", "ImportOpml",
        "ListFolders", "CreateFolder", "RenameFolder", "DeleteFolder", "MoveFolder",
        "GetCounts", "ListArticles", "GetArticle", "UpdateArticle", "UpdateArticles", "MarkAllRead", "LoadFullText", "Search",
        "ListBoards", "CreateBoard", "RenameBoard", "DeleteBoard", "AddToBoard", "RemoveFromBoard",
        "GetSettings", "UpdateSettings", "GetViewPreference", "SaveViewPreference",
        "SummariseArticle", "ListAiFeeds", "CreateAiFeed", "UpdateAiFeed", "DeleteAiFeed",
        "ListUsers", "CreateUser", "UpdateUser", "DeleteUser", "ListInvites", "CreateInvite", "RevokeInvite", "GetAdminSettings", "UpdateAdminSettings", "ListFeedHealth",
    ];

    [Fact]
    public async Task The_openapi_document_covers_every_ui_operation_and_needs_a_session()
    {
        await using var app = fixture.CreateFreshApp();
        using var anonymous = Browser.Open(app);
        (await anonymous.GetAsync("/api/docs/v1.json")).StatusCode.Should().Be(HttpStatusCode.Redirect, "the docs need a signed-in browser");

        using var browser = await SignedInBrowserAsync(app);
        var document = JsonDocument.Parse(await browser.GetHtmlAsync("/api/docs/v1.json")).RootElement;

        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject())
            .Where(o => o.Value.TryGetProperty("operationId", out _))
            .Select(o => o.Value.GetProperty("operationId").GetString())
            .ToList();
        operations.Should().Contain(UiOperations);
        document.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer").GetProperty("scheme").GetString().Should().Be("bearer");
        document.GetProperty("paths").EnumerateObject().Select(p => p.Name).Should().AllSatisfy(p => p.Should().StartWith("/api/v1/"));
    }

    [Fact]
    public async Task The_docs_page_gets_a_relaxed_csp_and_nothing_else_does()
    {
        await using var app = fixture.CreateFreshApp();
        using var browser = await SignedInBrowserAsync(app);

        (await browser.GetAsync("/api/docs")).Headers.Location!.ToString().Should().Be("docs/");
        var docs = await browser.GetAsync("/api/docs/");
        docs.StatusCode.Should().Be(HttpStatusCode.OK);
        docs.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self' 'unsafe-inline'").And.Contain("frame-ancestors 'none'");
        (await docs.Content.ReadAsStringAsync(Ct)).Should().NotContain("cdn.jsdelivr.net", "Scalar's assets are served from the app");

        var home = await browser.GetAsync("/");
        home.Headers.GetValues("Content-Security-Policy").Single().Should().NotContain("unsafe-inline");
    }

    private static async Task<Browser> SignedInBrowserAsync(SkimrFactory app)
    {
        var browser = Browser.Open(app);
        var code = app.Services.GetRequiredService<SetupService>().Code!;
        var response = await browser.SubmitAsync("/setup", "setup", new Dictionary<string, string>
        {
            ["Input.Code"] = code,
            ["Input.UserName"] = "admin",
            ["Input.Password"] = "correct horse battery",
            ["Input.ConfirmPassword"] = "correct horse battery",
            ["Input.Mode"] = "InviteOnly",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return browser;
    }

    internal sealed class ApiClient(HttpClient client, string token)
    {
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
            SendRawAsync(method, path, body is null ? null : JsonContent.Create(body));

        public Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content)
        {
            var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request, Ct);
        }

        public async Task<JsonElement> GetJsonAsync(string path)
        {
            var response = await SendAsync(HttpMethod.Get, path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }
    }
}

public sealed class SqliteRestApiTests(SqliteAppFixture fixture) : RestApiTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresRestApiTests(PostgresAppFixture fixture) : RestApiTests(fixture), IClassFixture<PostgresAppFixture>;
