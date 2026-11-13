using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skimr.Domain;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Fever API over HTTP, checked against the types and behaviour Fever clients expect (PLAN D61).</summary>
public abstract class FeverTests(AppFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Factory.Services;

    private ReadingService Reading => Services.GetRequiredService<ReadingService>();

    private LibraryService Library => Services.GetRequiredService<LibraryService>();

    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A user and the api_key a Fever client computes: md5("UserName:token").</summary>
    private async Task<(Guid Id, string Name, string Key)> NewUserAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var name = "Fe-" + Guid.NewGuid().ToString("N")[..8];
        var id = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync(name, "a long password here", false, Ct)).UserId!.Value;
        var token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync(id, "fever app", null, Ct)).PlainText;
        return (id, name, Md5(name + ":" + token));
    }

    private async Task<(string Url, List<long> Ids, long FeedId)> NewFeedAsync(int count)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        var url = $"https://fever-tests.example.com/{Guid.NewGuid():N}";
        var feed = new Feed { Url = url, SiteUrl = "https://fever-tests.example.com/", Title = "Fever feed", CreatedAt = DateTime.UtcNow, NextFetchAt = DateTime.UtcNow.AddDays(1), LastSuccessAt = DateTime.UtcNow };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(Ct);
        var articles = Enumerable.Range(0, count).Select(i => new Article
        {
            FeedId = feed.Id,
            DedupKey = Guid.NewGuid().ToString("N"),
            Title = $"Fever {i}",
            Url = $"https://fever-tests.example.com/{i}",
            ContentHtml = "<p>Hi <img src=\"http://images.example.com/f.png\"></p>",
            ContentHash = "h",
            PublishedAt = DateTime.UtcNow.AddHours(-i).AddMinutes(-1),
            FetchedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync(Ct);
        return (url, articles.Select(a => a.Id).OrderBy(i => i).ToList(), feed.Id);
    }

    private HttpClient Client() => fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });

    /// <summary>The usual Fever call: POST /api/fever?api&amp;… with api_key (and any marks) in a urlencoded body.</summary>
    private async Task<JsonElement> FeverAsync(string key, string query, params (string Key, string Value)[] form)
    {
        using var client = Client();
        var body = new FormUrlEncodedContent(form.Prepend((Key: "api_key", Value: key)).Select(f => KeyValuePair.Create(f.Key, f.Value)));
        var response = await client.PostAsync(new Uri("/api/fever?api" + query, UriKind.Relative), body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        json.GetProperty("api_version").GetInt32().Should().Be(3);
        return json;
    }

    private static List<long> Ids(JsonElement json, string property)
    {
        json.GetProperty(property).ValueKind.Should().Be(JsonValueKind.String, $"{property} is a comma-separated string");
        var text = json.GetProperty(property).GetString()!;
        return text.Length == 0 ? [] : text.Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToList();
    }

    // ---- Auth ---------------------------------------------------------------------------

    [Fact]
    public async Task A_wrong_key_still_gets_200_with_auth_0()
    {
        var (_, name, key) = await NewUserAsync();

        foreach (var bad in new[] { "", "0123456789abcdef0123456789abcdef", "not hex at all", key + "00", Md5(name.ToLowerInvariant() + ":x") })
        {
            var json = await FeverAsync(bad, "&groups&items");
            json.GetProperty("auth").GetInt32().Should().Be(0, bad);
            json.TryGetProperty("groups", out _).Should().BeFalse();
            json.TryGetProperty("items", out _).Should().BeFalse();
        }

        (await FeverAsync(key.ToUpperInvariant(), "")).GetProperty("auth").GetInt32().Should().Be(1, "hex case doesn't matter");
    }

    [Fact]
    public async Task The_key_works_urlencoded_multipart_or_in_the_query_string_on_either_path()
    {
        var (_, _, key) = await NewUserAsync();
        using var client = Client();

        async Task<int> AuthAsync(HttpResponseMessage response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("auth").GetInt32();
        }

        using var multipart = new MultipartFormDataContent { { new StringContent(key), "api_key" } };
        (await AuthAsync(await client.PostAsync(new Uri("/api/fever/?api", UriKind.Relative), multipart, Ct))).Should().Be(1);
        (await AuthAsync(await client.PostAsync(new Uri("/api/fever?api&api_key=" + key, UriKind.Relative), null, Ct))).Should().Be(1);
        (await AuthAsync(await client.GetAsync(new Uri("/api/fever/?api&api_key=" + key, UriKind.Relative), Ct))).Should().Be(1);
    }

    [Fact]
    public async Task A_token_created_before_fever_support_or_a_disabled_user_gets_auth_0()
    {
        var (userId, _, key) = await NewUserAsync();
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SkimrDbContext>().ApiTokens.Where(t => t.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.FeverKeyHash, (string?)null), Ct);
        }

        (await FeverAsync(key, "")).GetProperty("auth").GetInt32().Should().Be(0);

        var (otherId, _, otherKey) = await NewUserAsync();
        await using (var scope = Services.CreateAsyncScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateUserAsync("fe-admin-" + Guid.NewGuid().ToString("N")[..6], "a long password here", true, Ct)).UserId!.Value;
            (await scope.ServiceProvider.GetRequiredService<AccountService>().SetDisabledAsync(admin, otherId, true, Ct)).Succeeded.Should().BeTrue();
        }

        (await FeverAsync(otherKey, "")).GetProperty("auth").GetInt32().Should().Be(0);
    }

    // ---- Reading ------------------------------------------------------------------------

    [Fact]
    public async Task Groups_feeds_and_items_come_in_one_request_with_fever_types()
    {
        var (url, ids, _) = await NewFeedAsync(3);
        var (userId, _, key) = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(userId, "News", Ct)).Id!.Value;
        var sub = (await Library.SubscribeAsync(userId, url, [folder], Ct)).Id!.Value;
        await Reading.SetSavedAsync(userId, ids[0], true, Ct);

        var json = await FeverAsync(key, "&groups&feeds&items&links&unread_item_ids&saved_item_ids");

        json.GetProperty("auth").GetInt32().Should().Be(1);
        json.GetProperty("last_refreshed_on_time").GetInt64().Should().BeGreaterThan(0);
        var group = json.GetProperty("groups").EnumerateArray().Single();
        (group.GetProperty("id").GetInt64(), group.GetProperty("title").GetString()).Should().Be((folder, "News"));
        var feed = json.GetProperty("feeds").EnumerateArray().Single();
        feed.GetProperty("id").GetInt64().Should().Be(sub);
        feed.GetProperty("url").GetString().Should().Be(url);
        feed.GetProperty("site_url").GetString().Should().Be("https://fever-tests.example.com/");
        feed.GetProperty("is_spark").GetInt32().Should().Be(0);
        var feedsGroup = json.GetProperty("feeds_groups").EnumerateArray().Single();
        feedsGroup.GetProperty("group_id").GetInt64().Should().Be(folder);
        feedsGroup.GetProperty("feed_ids").GetString().Should().Be(sub.ToString(CultureInfo.InvariantCulture));

        var items = json.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetInt64()).Should().Equal(ids);
        var first = items[0];
        first.GetProperty("feed_id").GetInt64().Should().Be(sub);
        first.GetProperty("is_read").ValueKind.Should().Be(JsonValueKind.Number);
        first.GetProperty("is_saved").GetInt32().Should().Be(1);
        first.GetProperty("created_on_time").ValueKind.Should().Be(JsonValueKind.Number);
        first.GetProperty("html").GetString().Should().Contain("http://images.example.com/f.png").And.NotContain("/img/");
        json.GetProperty("total_items").GetInt32().Should().Be(3);
        json.GetProperty("links").GetArrayLength().Should().Be(0);
        Ids(json, "unread_item_ids").Should().BeEquivalentTo(ids);
        Ids(json, "saved_item_ids").Should().Equal(ids[0]);
    }

    [Fact]
    public async Task Since_id_and_max_id_page_through_every_item_exactly_once()
    {
        var (url, ids, _) = await NewFeedAsync(120);
        var (userId, _, key) = await NewUserAsync();
        await Library.SubscribeAsync(userId, url, [], Ct);
        var held = (await Reading.GetAllArticleIdsAsync(userId, new SyncFilter(Read: false), Ct)).Order().ToList();

        var up = new List<long>();
        long since = 0;
        while (true)
        {
            var page = (await FeverAsync(key, $"&items&since_id={since}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
            page.Count.Should().BeLessThanOrEqualTo(50);
            if (page.Count == 0)
            {
                break;
            }

            up.AddRange(page);
            since = page.Max();
        }

        up.Should().Equal(held);

        var down = new List<long>();
        var max = long.MaxValue;
        while (true)
        {
            var page = (await FeverAsync(key, $"&items&max_id={max}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
            if (page.Count == 0)
            {
                break;
            }

            down.AddRange(page);
            max = page.Min();
        }

        down.Order().Should().Equal(held);
        held.Should().NotBeEmpty();
        ids.Should().Contain(held);
    }

    [Fact]
    public async Task Back_filled_articles_below_the_clients_since_id_arrive_through_unread_ids_and_with_ids()
    {
        // Feed B's articles exist (fetched for someone else) before feed A's, so they have lower ids.
        var (urlB, idsB, _) = await NewFeedAsync(3);
        var (urlA, idsA, _) = await NewFeedAsync(3);
        var (userId, _, key) = await NewUserAsync();
        await Library.SubscribeAsync(userId, urlA, [], Ct);
        var synced = (await FeverAsync(key, "&items&since_id=0")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
        synced.Should().Equal(idsA);

        await Library.SubscribeAsync(userId, urlB, [], Ct);

        // PLAN D61: since_id never returns them...
        (await FeverAsync(key, $"&items&since_id={synced.Max()}")).GetProperty("items").GetArrayLength().Should().Be(0);
        // ...but the unread list does, and with_ids fetches them, which is how Fever clients fill gaps.
        var unread = Ids(await FeverAsync(key, "&unread_item_ids"), "unread_item_ids");
        var missing = unread.Except(synced).ToList();
        missing.Should().BeEquivalentTo(idsB);
        var fetched = (await FeverAsync(key, "&items&with_ids=" + string.Join(',', missing))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64());
        fetched.Should().BeEquivalentTo(idsB);
    }

    // ---- Marks --------------------------------------------------------------------------

    [Fact]
    public async Task Marks_change_state_and_return_the_updated_id_lists()
    {
        var (url, ids, _) = await NewFeedAsync(4);
        var (userId, _, key) = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(userId, "G", Ct)).Id!.Value;
        var sub = (await Library.SubscribeAsync(userId, url, [folder], Ct)).Id!.Value;
        string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

        Ids(await FeverAsync(key, "", ("mark", "item"), ("as", "read"), ("id", Id(ids[0]))), "unread_item_ids").Should().NotContain(ids[0]);
        Ids(await FeverAsync(key, "", ("mark", "item"), ("as", "unread"), ("id", Id(ids[0]))), "unread_item_ids").Should().Contain(ids[0]);
        Ids(await FeverAsync(key, "", ("mark", "item"), ("as", "saved"), ("id", Id(ids[1]))), "saved_item_ids").Should().Equal(ids[1]);
        Ids(await FeverAsync(key, "", ("mark", "item"), ("as", "unsaved"), ("id", Id(ids[1]))), "saved_item_ids").Should().BeEmpty();

        // Feed read before a time: ids are ascending by insert, and insert i was published i hours ago.
        var before = DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-30).ToUnixTimeSeconds();
        var unread = Ids(await FeverAsync(key, "", ("mark", "feed"), ("as", "read"), ("id", Id(sub)), ("before", Id(before))), "unread_item_ids");
        unread.Should().BeEquivalentTo([ids[0], ids[1]]);

        // Group -1 (sparks) does nothing; group 0 is everything.
        Ids(await FeverAsync(key, "", ("mark", "group"), ("as", "read"), ("id", "-1"), ("before", Id(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))), "unread_item_ids").Should().HaveCount(2);
        Ids(await FeverAsync(key, "", ("mark", "group"), ("as", "read"), ("id", "0"), ("before", Id(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))), "unread_item_ids").Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_input_still_answers_200()
    {
        var (_, _, key) = await NewUserAsync();

        foreach (var query in new[] { "&items&since_id=abc", "&items&max_id=-5", "&items&with_ids=,,x,-1,99999999999999999999", "&mark=item&as=read&id=abc", "&mark=feed&as=read&id=1&before=notatime", "&mark=nonsense&as=x&id=1" })
        {
            (await FeverAsync(key, query)).GetProperty("auth").GetInt32().Should().Be(1, query);
        }
    }

    [Fact]
    public async Task Favicons_are_base64_with_a_mime_prefix_and_no_data_scheme()
    {
        var (url, _, feedId) = await NewFeedAsync(1);
        var (userId, _, key) = await NewUserAsync();
        await Library.SubscribeAsync(userId, url, [], Ct);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        long iconId;
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<StorageOptions>();
            var name = Guid.NewGuid().ToString("N") + ".png";
            Directory.CreateDirectory(storage.IconDirectory);
            await File.WriteAllBytesAsync(Path.Combine(storage.IconDirectory, name), png, Ct);
            var icon = new Icon { Hash = Guid.NewGuid().ToString("N"), MimeType = "image/png", FileName = name, CreatedAt = DateTime.UtcNow };
            db.Icons.Add(icon);
            await db.SaveChangesAsync(Ct);
            iconId = icon.Id;
            await db.Feeds.Where(f => f.Id == feedId).ExecuteUpdateAsync(s => s.SetProperty(f => f.IconId, iconId), Ct);
        }

        var json = await FeverAsync(key, "&feeds&favicons");

        json.GetProperty("feeds").EnumerateArray().Single().GetProperty("favicon_id").GetInt64().Should().Be(iconId);
        var favicon = json.GetProperty("favicons").EnumerateArray().Single();
        favicon.GetProperty("id").GetInt64().Should().Be(iconId);
        favicon.GetProperty("data").GetString().Should().Be("image/png;base64," + Convert.ToBase64String(png));
    }

    // ---- Ownership ----------------------------------------------------------------------

    [Fact]
    public async Task Another_users_items_feeds_and_groups_are_out_of_reach()
    {
        var (url, ids, _) = await NewFeedAsync(2);
        var (aliceId, _, _) = await NewUserAsync();
        var folder = (await Library.CreateFolderAsync(aliceId, "Private", Ct)).Id!.Value;
        var sub = (await Library.SubscribeAsync(aliceId, url, [folder], Ct)).Id!.Value;
        var (_, _, bob) = await NewUserAsync();
        string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        var now = Id(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        (await FeverAsync(bob, "&items&with_ids=" + string.Join(',', ids))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await FeverAsync(bob, "&items&since_id=0")).GetProperty("items").GetArrayLength().Should().Be(0);
        await FeverAsync(bob, "", ("mark", "item"), ("as", "read"), ("id", Id(ids[0])));
        await FeverAsync(bob, "", ("mark", "item"), ("as", "saved"), ("id", Id(ids[0])));
        await FeverAsync(bob, "", ("mark", "feed"), ("as", "read"), ("id", Id(sub)), ("before", now));
        await FeverAsync(bob, "", ("mark", "group"), ("as", "read"), ("id", Id(folder)), ("before", now));
        var bobView = await FeverAsync(bob, "&groups&feeds&unread_item_ids&saved_item_ids");
        bobView.GetProperty("groups").GetArrayLength().Should().Be(0);
        bobView.GetProperty("feeds").GetArrayLength().Should().Be(0);
        Ids(bobView, "unread_item_ids").Should().BeEmpty();
        Ids(bobView, "saved_item_ids").Should().BeEmpty();

        foreach (var id in ids)
        {
            var article = (await Reading.GetArticleAsync(aliceId, id, Ct))!;
            (article.IsRead, article.IsSaved).Should().Be((false, false));
        }
    }
}

public sealed class SqliteFeverTests(SqliteAppFixture fixture) : FeverTests(fixture), IClassFixture<SqliteAppFixture>;

public sealed class PostgresFeverTests(PostgresAppFixture fixture) : FeverTests(fixture), IClassFixture<PostgresAppFixture>;
