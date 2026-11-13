using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;
using Skimr.UnitTests.Fetching;

namespace Skimr.IntegrationTests;

/// <summary>Google Reader API over HTTP, in the order real clients call it (PLAN Phase 6, D60).</summary>
public abstract class GoogleReaderTests(AppFixture fixture)
{
    private const string Api = "/api/greader/reader/api/0";
    private const string ReadingList = "user/-/state/com.google/reading-list";
    private const string ReadTag = "user/-/state/com.google/read";
    private const string Starred = "user/-/state/com.google/starred";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Factory.Services;

    private ReadingService Reading => Services.GetRequiredService<ReadingService>();

    private LibraryService Library => Services.GetRequiredService<LibraryService>();

    private async Task<(Guid Id, string Name, string Token)> NewUserAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var name = "GR-" + Guid.NewGuid().ToString("N")[..8];
        var id = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync(name, "a long password here", false, Ct)).UserId!.Value;
        var token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync(id, "reader app", null, Ct)).PlainText;
        return (id, name, token);
    }

    private async Task<(string Url, List<long> Ids)> NewFeedAsync(int count)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        var url = $"https://greader-tests.example.com/{Guid.NewGuid():N}";
        var feed = new Feed { Url = url, SiteUrl = "https://greader-tests.example.com/", Title = "GR feed", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1) };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        var articles = Enumerable.Range(0, count).Select(i => new Article
        {
            FeedId = feed.Id,
            DedupKey = Guid.NewGuid().ToString("N"),
            Title = $"Item {i}",
            Url = $"https://greader-tests.example.com/{i}",
            ContentHtml = "<p>Hi <img src=\"http://images.example.com/x.png\"></p>",
            ContentHash = "h",
            PublishedAt = DateTime.UtcNow.AddHours(-i).AddMinutes(-1),
            FetchedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync(Ct);
        return (url, articles.Select(a => a.Id).ToList());
    }

    private HttpClient Client(string? token = null)
    {
        var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse("GoogleLogin auth=" + token);
        }

        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, params (string Key, string Value)[] form) =>
        client.PostAsync(new Uri(Api + path, UriKind.Relative), new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Key, f.Value))), Ct);

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => client.GetAsync(new Uri(Api + path, UriKind.Relative), Ct);

    private static string LongId(long id) => "tag:google.com,2005:reader/item/" + id.ToString("x16", CultureInfo.InvariantCulture);

    // ---- The NetNewsWire sync, step by step ---------------------------------------------

    [Fact]
    public async Task A_client_syncs_in_netnewswire_order()
    {
        var (url, ids) = await NewFeedAsync(5);
        var (userId, name, token) = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(userId, "Tech / Rust", Ct)).Id!.Value;
        var sub = (await Library.SubscribeAsync(userId, url, [folder], Ct)).Id!.Value;
        await Reading.SetReadAsync(userId, [ids[4]], true, Ct);

        // 1. ClientLogin: user name plus an API token as the password. Any letter case for the name.
        using var anonymous = Client();
        var login = await anonymous.PostAsync(new Uri("/api/greader/accounts/ClientLogin", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = name.ToLowerInvariant(), ["Passwd"] = token }), Ct);
        var body = await login.Content.ReadAsStringAsync(Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var auth = body.Split('\n').Single(l => l.StartsWith("Auth=", StringComparison.Ordinal))["Auth=".Length..];
        using var client = Client(auth);

        // 2–3. Token and user info.
        (await (await GetAsync(client, "/token")).Content.ReadAsStringAsync(Ct)).Should().NotBeEmpty();
        var info = await JsonAsync(await GetAsync(client, "/user-info?output=json"));
        info.GetProperty("userName").GetString().Should().Be(name);
        var numericUserId = info.GetProperty("userId").GetString()!;

        // 4. Subscriptions with their labels.
        var subscriptions = (await JsonAsync(await GetAsync(client, "/subscription/list?output=json"))).GetProperty("subscriptions");
        var s = subscriptions.EnumerateArray().Single();
        s.GetProperty("id").GetString().Should().Be($"feed/{sub}");
        s.GetProperty("url").GetString().Should().Be(url);
        s.GetProperty("htmlUrl").GetString().Should().Be("https://greader-tests.example.com/");
        s.GetProperty("categories").EnumerateArray().Single().GetProperty("id").GetString().Should().Be("user/-/label/Tech / Rust");

        // 5. Tags.
        var tags = (await JsonAsync(await GetAsync(client, "/tag/list?output=json"))).GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        tags.Should().Contain([Starred, "user/-/label/Tech / Rust"]);

        // 6. Unread counts: reading list, feed and label, each with the newest unread time.
        var counts = (await JsonAsync(await GetAsync(client, "/unread-count?output=json"))).GetProperty("unreadcounts").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
        counts[ReadingList].GetProperty("count").GetInt32().Should().Be(4);
        counts[$"feed/{sub}"].GetProperty("count").GetInt32().Should().Be(4);
        counts["user/-/label/Tech / Rust"].GetProperty("count").GetInt32().Should().Be(4);
        long.Parse(counts[ReadingList].GetProperty("newestItemTimestampUsec").GetString()!, CultureInfo.InvariantCulture).Should().BeGreaterThan(0);

        // 7. Unread ids, using the client's own user id form of the stream.
        var refs = (await JsonAsync(await GetAsync(client, $"/stream/items/ids?output=json&s=user/{numericUserId}/state/com.google/reading-list&xt={ReadTag}&n=1000")))
            .GetProperty("itemRefs").EnumerateArray().ToList();
        refs.Select(r => long.Parse(r.GetProperty("id").GetString()!, CultureInfo.InvariantCulture)).Should().Equal(ids.Take(4));
        refs[0].GetProperty("timestampUsec").ValueKind.Should().Be(JsonValueKind.String);

        // 8. Contents for those ids, mixing the long and short id forms.
        var contents = await JsonAsync(await PostAsync(client, "/stream/items/contents?output=json",
            ("i", LongId(ids[0])), ("i", ids[1].ToString(CultureInfo.InvariantCulture)), ("i", ids[2].ToString(CultureInfo.InvariantCulture))));
        var items = contents.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetString()).Should().Equal(LongId(ids[0]), LongId(ids[1]), LongId(ids[2]));
        var first = items[0];
        first.GetProperty("published").ValueKind.Should().Be(JsonValueKind.Number);
        first.GetProperty("crawlTimeMsec").ValueKind.Should().Be(JsonValueKind.String);
        first.GetProperty("origin").GetProperty("streamId").GetString().Should().Be($"feed/{sub}");
        first.GetProperty("alternate")[0].GetProperty("href").GetString().Should().Be("https://greader-tests.example.com/0");
        first.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).Should().Contain("user/-/label/Tech / Rust").And.NotContain(ReadTag);
        first.GetProperty("summary").GetProperty("content").GetString().Should().Contain("http://images.example.com/x.png").And.NotContain("/img/");

        // 9. Mark one read and one starred, then check through the reader's own service.
        (await PostAsync(client, "/edit-tag", ("i", LongId(ids[0])), ("a", ReadTag))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, "/edit-tag", ("i", ids[1].ToString(CultureInfo.InvariantCulture)), ("a", Starred))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Reading.GetArticleAsync(userId, ids[0], Ct))!.IsRead.Should().BeTrue();
        (await Reading.GetArticleAsync(userId, ids[1], Ct))!.IsSaved.Should().BeTrue();
        (await PostAsync(client, "/edit-tag", ("i", LongId(ids[0])), ("r", ReadTag))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Reading.GetArticleAsync(userId, ids[0], Ct))!.IsRead.Should().BeFalse();

        var starred = (await JsonAsync(await GetAsync(client, $"/stream/items/ids?output=json&s={Starred}&n=1000"))).GetProperty("itemRefs");
        starred.EnumerateArray().Select(r => r.GetProperty("id").GetString()).Should().Equal(ids[1].ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Client_login_rejects_a_wrong_token_or_a_name_that_isnt_the_tokens_owner()
    {
        var (_, name, token) = await NewUserAsync();
        var (_, otherName, _) = await NewUserAsync();
        using var client = Client();

        foreach (var (email, passwd) in new[] { (name, "skimr_wrong"), (otherName, token), (name, "a long password here") })
        {
            var response = await client.PostAsync(new Uri("/api/greader/accounts/ClientLogin", UriKind.Relative),
                new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email, ["Passwd"] = passwd }), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Error=BadAuthentication");
        }

        (await client.GetAsync(new Uri(Api + "/user-info", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Streams and paging -------------------------------------------------------------

    [Fact]
    public async Task Continuation_returns_every_id_exactly_once()
    {
        var (url, ids) = await NewFeedAsync(11);
        var (userId, _, token) = await NewUserAsync();
        var sub = (await Library.SubscribeAsync(userId, url, [], Ct)).Id!.Value;
        using var client = Client(token);

        foreach (var (order, expected) in new[] { ("", ids), ("&r=o", ids.AsEnumerable().Reverse().ToList()) })
        {
            var seen = new List<long>();
            string? continuation = null;
            do
            {
                var page = await JsonAsync(await GetAsync(client, $"/stream/items/ids?s=feed/{sub}&n=4{order}" + (continuation is null ? "" : "&c=" + continuation)));
                seen.AddRange(page.GetProperty("itemRefs").EnumerateArray().Select(r => long.Parse(r.GetProperty("id").GetString()!, CultureInfo.InvariantCulture)));
                continuation = page.TryGetProperty("continuation", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            }
            while (continuation is not null);

            seen.Should().Equal(expected);
        }

        (await GetAsync(client, $"/stream/items/ids?s=feed/{sub}&c=garbage!")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(client, $"/stream/items/ids?s=feed/{sub}&n=-1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(client, $"/stream/items/ids?s=nonsense/stream")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stream_contents_takes_a_label_with_a_slash_in_the_path_encoded_or_not()
    {
        var (url, ids) = await NewFeedAsync(2);
        var (userId, _, token) = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(userId, "Tech / Rust", Ct)).Id!.Value;
        await Library.SubscribeAsync(userId, url, [folder], Ct);
        using var client = Client(token);

        foreach (var path in new[]
        {
            "/stream/contents/user%2F-%2Flabel%2FTech%20%2F%20Rust",
            "/stream/contents/user/-/label/Tech%20%2F%20Rust",
            "/stream/contents/user/-/label/Tech%20/%20Rust",
            "/stream/contents?s=" + Uri.EscapeDataString("user/-/label/Tech / Rust"),
        })
        {
            var stream = await JsonAsync(await GetAsync(client, path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "n=10"));
            stream.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal([LongId(ids[0]), LongId(ids[1])], path);
        }
    }

    [Fact]
    public async Task Filters_ot_it_and_xt_narrow_the_stream()
    {
        var (url, ids) = await NewFeedAsync(4);
        var (userId, _, token) = await NewUserAsync();
        var sub = (await Library.SubscribeAsync(userId, url, [], Ct)).Id!.Value;
        await Reading.SetReadAsync(userId, [ids[0]], true, Ct);
        await Reading.SetSavedAsync(userId, ids[1], true, Ct);
        using var client = Client(token);

        async Task<List<long>> IdsAsync(string query) =>
            (await JsonAsync(await GetAsync(client, $"/stream/items/ids?s=feed/{sub}&n=100&{query}"))).GetProperty("itemRefs").EnumerateArray()
                .Select(r => long.Parse(r.GetProperty("id").GetString()!, CultureInfo.InvariantCulture)).ToList();

        (await IdsAsync($"it={ReadTag}")).Should().Equal(ids[0]);
        (await IdsAsync($"it={Starred}")).Should().Equal(ids[1]);
        (await IdsAsync($"xt={Starred}&xt={ReadTag}")).Should().Equal(ids[2], ids[3]);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-30).ToUnixTimeSeconds();
        (await IdsAsync($"ot={cutoff}")).Should().Equal(ids[0], ids[1], ids[2]);
        (await IdsAsync($"nt={cutoff}")).Should().Equal(ids[3]);
    }

    [Fact]
    public async Task Edit_tag_takes_more_ids_than_the_default_form_limit()
    {
        var (url, ids) = await NewFeedAsync(3);
        var (userId, _, token) = await NewUserAsync();
        await Library.SubscribeAsync(userId, url, [], Ct);
        using var client = Client(token);

        // 1,500 values: the real ids plus ids that aren't the user's (ignored).
        var form = ids.Select(i => ("i", i.ToString(CultureInfo.InvariantCulture)))
            .Concat(Enumerable.Range(1, 1497).Select(i => ("i", (long.MaxValue - i).ToString(CultureInfo.InvariantCulture))))
            .Append(("a", ReadTag))
            .ToArray();
        (await PostAsync(client, "/edit-tag", form)).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var id in ids)
        {
            (await Reading.GetArticleAsync(userId, id, Ct))!.IsRead.Should().BeTrue();
        }

        (await PostAsync(client, "/edit-tag", ("i", "not-an-id"), ("a", ReadTag))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Mark_all_as_read_stops_at_the_timestamp_in_microseconds()
    {
        var (url, ids) = await NewFeedAsync(4);
        var (userId, _, token) = await NewUserAsync();
        var sub = (await Library.SubscribeAsync(userId, url, [], Ct)).Id!.Value;
        using var client = Client(token);
        var cutoffUsec = DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-30).ToUnixTimeMilliseconds() * 1000;

        (await PostAsync(client, "/mark-all-as-read", ("s", $"feed/{sub}"), ("ts", cutoffUsec.ToString(CultureInfo.InvariantCulture)))).StatusCode.Should().Be(HttpStatusCode.OK);

        var read = new List<bool>();
        foreach (var id in ids)
        {
            read.Add((await Reading.GetArticleAsync(userId, id, Ct))!.IsRead);
        }

        read.Should().Equal(false, false, true, true);
    }

    // ---- Subscriptions and labels -------------------------------------------------------

    [Fact]
    public async Task Subscriptions_and_labels_can_be_managed()
    {
        var (url, _) = await NewFeedAsync(1);
        var (url2, _) = await NewFeedAsync(1);
        var (userId, _, token) = await NewUserAsync();
        using var client = Client(token);

        (await PostAsync(client, "/subscription/edit", ("ac", "subscribe"), ("s", $"feed/{url}"), ("t", "Named"), ("a", "user/-/label/News"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, "/subscription/edit", ("ac", "subscribe"), ("s", $"feed/{url2}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = (await Library.ListSourcesAsync(userId, Ct)).Single(s => s.FeedUrl == url2).SubscriptionId;

        var sources = await Library.ListSourcesAsync(userId, Ct);
        var named = sources.Single(s => s.FeedUrl == url);
        // On subscribe, t is the client's guess at the feed's name: a placeholder until the first fetch, not a rename.
        named.CustomTitle.Should().BeNull();
        var folders = await Library.ListFoldersAsync(userId, Ct);
        named.FolderIds.Should().Equal(folders.Single(f => f.Name == "News").Id);

        // Edit: rename, move from News to Later; then rename the label and delete it.
        (await PostAsync(client, "/subscription/edit", ("ac", "edit"), ("s", $"feed/{named.SubscriptionId}"), ("t", "Renamed"), ("a", "user/-/label/Later"), ("r", "user/-/label/News"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, "/rename-tag", ("s", "user/-/label/Later"), ("dest", "user/-/label/Someday"))).StatusCode.Should().Be(HttpStatusCode.OK);
        folders = await Library.ListFoldersAsync(userId, Ct);
        named = (await Library.ListSourcesAsync(userId, Ct)).Single(s => s.FeedUrl == url);
        named.CustomTitle.Should().Be("Renamed");
        named.FolderIds.Should().Equal(folders.Single(f => f.Name == "Someday").Id);
        (await PostAsync(client, "/disable-tag", ("s", "user/-/label/Someday"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Library.ListFoldersAsync(userId, Ct)).Select(f => f.Name).Should().NotContain("Someday");

        (await PostAsync(client, "/subscription/edit", ("ac", "unsubscribe"), ("s", $"feed/{second}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Library.ListSourcesAsync(userId, Ct)).Select(s => s.FeedUrl).Should().Equal(url);
    }

    [Fact]
    public async Task Quick_add_finds_the_feed_behind_a_site_address()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/", (HttpContext c) => Results.Content(
                $"<html><head><link rel=\"alternate\" type=\"application/rss+xml\" title=\"Site feed\" href=\"/feed.xml\"></head><body>Hi</body></html>", "text/html"));
            app.MapGet("/feed.xml", () => Results.Text("<rss version=\"2.0\"><channel><title>Site feed</title></channel></rss>", "application/rss+xml"));
        });
        // A separate host: discovery also probes common paths like /feed.xml on the same site.
        await using var plain = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/", () => Results.Content("<html><body>No feed here</body></html>", "text/html")));
        await using var app = fixture.CreateFreshApp(("Fetch:AllowPrivateNetworkFeeds", "true"));
        Guid userId;
        string token;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            userId = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync("quick", "a long password here", false, Ct)).UserId!.Value;
            token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync(userId, "t", null, Ct)).PlainText;
        }

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse("GoogleLogin auth=" + token);

        var quick = await JsonAsync(await PostAsync(client, "/subscription/quickadd", ("quickadd", server.Url("").AbsoluteUri)));
        quick.GetProperty("numResults").GetInt32().Should().Be(1);
        quick.GetProperty("streamName").GetString().Should().Be("Site feed");
        var sources = await app.Services.GetRequiredService<LibraryService>().ListSourcesAsync(userId, Ct);
        sources.Single().FeedUrl.Should().Be(server.Url("feed.xml").AbsoluteUri);
        quick.GetProperty("streamId").GetString().Should().Be($"feed/{sources.Single().SubscriptionId}");

        var none = await JsonAsync(await PostAsync(client, "/subscription/quickadd", ("quickadd", plain.Url("").AbsoluteUri)));
        none.GetProperty("numResults").GetInt32().Should().Be(0);
        (await app.Services.GetRequiredService<LibraryService>().ListSourcesAsync(userId, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Edit_tag_handles_the_full_ten_thousand_ids()
    {
        // The cap is 10,000 ids per request; make sure both databases accept that many in one query.
        var (url, ids) = await NewFeedAsync(GoogleReaderApiMaxIds);
        var (userId, _, token) = await NewUserAsync();
        await Library.SubscribeAsync(userId, url, [], Ct);
        using var client = Client(token);

        var form = ids.Select(i => ("i", i.ToString(CultureInfo.InvariantCulture))).Append(("a", ReadTag)).ToArray();
        (await PostAsync(client, "/edit-tag", form)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Reading.GetNavAsync(userId, Ct)).AllUnread.Should().Be(0);
        var tooMany = ids.Select(i => ("i", i.ToString(CultureInfo.InvariantCulture))).Append(("i", "1")).Append(("a", ReadTag)).ToArray();
        (await PostAsync(client, "/edit-tag", tooMany)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private const int GoogleReaderApiMaxIds = 10_000;

    // ---- Ownership ----------------------------------------------------------------------

    [Fact]
    public async Task Another_users_streams_items_and_labels_are_out_of_reach()
    {
        var (url, ids) = await NewFeedAsync(2);
        var (aliceId, _, _) = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(aliceId, "Private", Ct)).Id!.Value;
        var sub = (await Library.SubscribeAsync(aliceId, url, [folder], Ct)).Id!.Value;
        var (_, _, bobToken) = await NewUserAsync();
        using var bob = Client(bobToken);
        var idForms = ids.Select(i => ("i", i.ToString(CultureInfo.InvariantCulture))).ToArray();

        (await JsonAsync(await GetAsync(bob, $"/stream/items/ids?s=feed/{sub}&n=100"))).GetProperty("itemRefs").GetArrayLength().Should().Be(0);
        (await JsonAsync(await GetAsync(bob, $"/stream/contents/feed/{sub}"))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await JsonAsync(await GetAsync(bob, $"/stream/contents/feed/{Uri.EscapeDataString(url)}"))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await JsonAsync(await GetAsync(bob, "/stream/contents/user/-/label/Private"))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await JsonAsync(await PostAsync(bob, "/stream/items/contents", idForms))).GetProperty("items").GetArrayLength().Should().Be(0);
        await PostAsync(bob, "/edit-tag", [.. idForms, ("a", ReadTag), ("a", Starred)]);
        await PostAsync(bob, "/mark-all-as-read", ("s", $"feed/{sub}"));
        await PostAsync(bob, "/mark-all-as-read", ("s", "user/-/label/Private"));
        (await PostAsync(bob, "/subscription/edit", ("ac", "unsubscribe"), ("s", $"feed/{sub}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostAsync(bob, "/subscription/edit", ("ac", "edit"), ("s", $"feed/{sub}"), ("t", "pwned"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostAsync(bob, "/rename-tag", ("s", "user/-/label/Private"), ("dest", "user/-/label/pwned"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostAsync(bob, "/disable-tag", ("s", "user/-/label/Private"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        foreach (var id in ids)
        {
            var article = (await Reading.GetArticleAsync(aliceId, id, Ct))!;
            (article.IsRead, article.IsSaved).Should().Be((false, false));
        }

        var source = (await Library.ListSourcesAsync(aliceId, Ct)).Single();
        source.CustomTitle.Should().BeNull();
        (await Library.ListFoldersAsync(aliceId, Ct)).Single().Name.Should().Be("Private");
    }

    [Fact]
    public async Task A_session_cookie_is_not_enough()
    {
        var (_, _, token) = await NewUserAsync();
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("x:y"u8.ToArray()));

        (await GetAsync(client, "/subscription/list")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var bearer = Client();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await GetAsync(bearer, "/subscription/list")).StatusCode.Should().Be(HttpStatusCode.OK, "bearer works here too");
    }
}

public sealed class SqliteGoogleReaderTests(SqliteAppFixture fixture) : GoogleReaderTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresGoogleReaderTests(PostgresAppFixture fixture) : GoogleReaderTests(fixture), IClassFixture<PostgresAppFixture>;
