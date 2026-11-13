using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Skimr.Domain;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Reading;
using Skimr.Web.Auth;

namespace Skimr.Web.Api;

/// <summary>
/// Google Reader API, as spoken by NetNewsWire, Reeder, ReadKit, FeedMe and friends (PLAN D60).
/// Clients sign in with ClientLogin using their user name and an API token as the password, then
/// send <c>Authorization: GoogleLogin auth=&lt;token&gt;</c>. Folders are labels; Read Later is "starred".
/// </summary>
internal static partial class GoogleReaderApi
{
    public const string BasePath = "/api/greader";
    public const int MaxIds = 10_000;
    public const int MaxItems = 1_000;
    private const long MaxFormBytes = 1024 * 1024;

    public const string ReadingList = "user/-/state/com.google/reading-list";
    public const string Read = "user/-/state/com.google/read";
    public const string Starred = "user/-/state/com.google/starred";
    public const string LabelPrefix = "user/-/label/";
    public const string FeedPrefix = "feed/";
    private const string LongIdPrefix = "tag:google.com,2005:reader/item/";

    public static IEndpointRouteBuilder MapGoogleReaderApi(this IEndpointRouteBuilder app)
    {
        // Anonymous by design: this is how clients get a token-bearing header in the first place.
        // POST only: the login rate limiter counts POSTs, and passwords belong in bodies, not URLs.
        app.MapPost(BasePath + "/accounts/ClientLogin", ClientLoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(Policies.LoginRateLimit)
            .DisableAntiforgery()
            .WithMetadata(new SkipStatusCodePagesAttribute())
            .ExcludeFromDescription();

        var api = app.MapGroup(BasePath + "/reader/api/0")
            .RequireAuthorization(Policies.Api)
            .RequireRateLimiting(Policies.ApiRateLimit)
            .DisableAntiforgery()
            .WithMetadata(new SkipStatusCodePagesAttribute())
            .ExcludeFromDescription();

        // T tokens guard against CSRF on cookie sessions; with header-only auth they add nothing.
        api.MapGet("/token", () => Results.Text("skimr-greader-token-not-needed-with-header-auth"));
        api.MapGet("/user-info", (ClaimsPrincipal user) => Results.Json(new
        {
            userId = UserId(user).ToString(),
            userName = user.Identity!.Name,
            userProfileId = UserId(user).ToString(),
            userEmail = "",
        }));
        api.MapGet("/subscription/list", SubscriptionListAsync);
        api.MapGet("/tag/list", TagListAsync);
        api.MapGet("/unread-count", UnreadCountAsync);
        api.MapMethods("/stream/items/ids", ["GET", "POST"], ItemIdsAsync);
        api.MapMethods("/stream/items/contents", ["GET", "POST"], ItemContentsAsync);
        api.MapGet("/stream/contents/{**streamId}", StreamContentsAsync);
        api.MapPost("/edit-tag", EditTagAsync);
        api.MapPost("/mark-all-as-read", MarkAllAsReadAsync);
        api.MapPost("/subscription/edit", SubscriptionEditAsync);
        api.MapPost("/subscription/quickadd", QuickAddAsync);
        api.MapPost("/rename-tag", RenameTagAsync);
        api.MapPost("/disable-tag", DisableTagAsync);
        return app;
    }

    // ---- Sign-in -----------------------------------------------------------------------------

    private static async Task<IResult> ClientLoginAsync(HttpContext context, ApiTokenService tokens, UserManager<SkimrUser> users)
    {
        var form = await ParamsAsync(context);
        if (form is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var userName = form["Email"].ToString().Trim();
        var token = form["Passwd"].ToString().Trim();
        var valid = await tokens.ValidateAsync(token, context.RequestAborted);
        var user = valid is null ? null : await users.FindByIdAsync(valid.Value.UserId.ToString());

        // The user name must match the token's owner, so a leaked token alone isn't a login for any name.
        if (user is null || AccountService.IsDisabled(user)
            || !string.Equals(user.NormalizedUserName, users.NormalizeName(userName), StringComparison.Ordinal))
        {
            return Results.Text("Error=BadAuthentication\n", "text/plain", statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Text($"SID={token}\nLSID={token}\nAuth={token}\n", "text/plain");
    }

    // ---- Lists -------------------------------------------------------------------------------

    private static async Task<IResult> SubscriptionListAsync(ClaimsPrincipal user, LibraryService library, CancellationToken ct)
    {
        var userId = UserId(user);
        var folders = (await library.ListFoldersAsync(userId, ct)).ToDictionary(f => f.Id, f => f.Name);
        var sources = await library.ListSourcesAsync(userId, ct);
        return Results.Json(new
        {
            subscriptions = sources.Select(s => new
            {
                id = FeedPrefix + s.SubscriptionId.ToString(CultureInfo.InvariantCulture),
                title = s.CustomTitle ?? s.Title,
                categories = s.FolderIds.Where(folders.ContainsKey).Select(f => new { id = LabelPrefix + folders[f], label = folders[f] }),
                url = s.FeedUrl,
                htmlUrl = s.SiteUrl ?? "",
                iconUrl = "",
            }),
        });
    }

    private static async Task<IResult> TagListAsync(ClaimsPrincipal user, LibraryService library, CancellationToken ct)
    {
        var folders = await library.ListFoldersAsync(UserId(user), ct);
        return Results.Json(new
        {
            tags = new object[] { new { id = Starred } }
                .Concat(folders.Select(f => new { id = LabelPrefix + f.Name, type = "folder" })),
        });
    }

    private static async Task<IResult> UnreadCountAsync(ClaimsPrincipal user, ReadingService reading, LibraryService library, CancellationToken ct)
    {
        var userId = UserId(user);
        var perFeed = await reading.GetUnreadByFeedAsync(userId, ct);
        var folders = (await library.ListFoldersAsync(userId, ct)).ToDictionary(f => f.Id, f => f.Name);
        var sources = (await library.ListSourcesAsync(userId, ct)).ToDictionary(s => s.SubscriptionId);

        var counts = new List<object>();
        if (perFeed.Count > 0)
        {
            counts.Add(Count(ReadingList, perFeed.Sum(f => f.Count), perFeed.Max(f => f.Newest)));
        }

        counts.AddRange(perFeed.Select(f => Count(FeedPrefix + f.SubscriptionId.ToString(CultureInfo.InvariantCulture), f.Count, f.Newest)));
        counts.AddRange(perFeed
            .SelectMany(f => sources.TryGetValue(f.SubscriptionId, out var s) ? s.FolderIds.Where(folders.ContainsKey).Select(id => (Folder: id, Feed: f)) : [])
            .GroupBy(x => x.Folder)
            .Select(g => Count(LabelPrefix + folders[g.Key], g.Sum(x => x.Feed.Count), g.Max(x => x.Feed.Newest))));

        return Results.Json(new { max = MaxIds, unreadcounts = counts });

        static object Count(string id, int count, DateTime newest) =>
            new { id, count, newestItemTimestampUsec = Usec(newest).ToString(CultureInfo.InvariantCulture) };
    }

    // ---- Streams -----------------------------------------------------------------------------

    private static async Task<IResult> ItemIdsAsync(HttpContext context, ClaimsPrincipal user, ReadingService reading, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var query = await StreamQueryAsync(UserId(user), p["s"].ToString(), p, MaxIds, library, context.RequestAborted);
        if (query is null)
        {
            return Results.BadRequest();
        }

        var page = await reading.GetArticleRefsAsync(UserId(user), query.View, query.Filter, query.Sort, query.After, query.Count, context.RequestAborted);
        return Results.Json(new
        {
            itemRefs = page.Items.Select(i => new
            {
                id = i.ArticleId.ToString(CultureInfo.InvariantCulture),
                directStreamIds = Array.Empty<string>(),
                timestampUsec = Usec(i.PublishedAt).ToString(CultureInfo.InvariantCulture),
            }),
            continuation = ApiCursor.Encode(page.Next),
        });
    }

    private static async Task<IResult> StreamContentsAsync(string? streamId, HttpContext context, ClaimsPrincipal user, ReadingService reading, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Routing leaves %2F encoded inside catch-all values, so labels like "Tech / Rust" need this.
        var stream = string.IsNullOrEmpty(streamId) ? p["s"].ToString() : Uri.UnescapeDataString(streamId);
        var userId = UserId(user);
        var query = await StreamQueryAsync(userId, stream, p, MaxItems, library, context.RequestAborted);
        if (query is null)
        {
            return Results.BadRequest();
        }

        var page = await reading.GetArticleRefsAsync(userId, query.View, query.Filter, query.Sort, query.After, query.Count, context.RequestAborted);
        var items = await reading.GetSyncItemsAsync(userId, page.Items.Select(i => i.ArticleId).ToList(), context.RequestAborted);
        var labels = await LabelsBySubscriptionAsync(userId, library, context.RequestAborted);
        var ordered = query.Sort == ArticleSort.Oldest ? items.Reverse() : items;
        return Results.Json(new
        {
            id = string.IsNullOrEmpty(stream) ? ReadingList : stream,
            updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            items = ordered.Select(i => ToItem(i, labels)),
            continuation = ApiCursor.Encode(page.Next),
        });
    }

    private static async Task<IResult> ItemContentsAsync(HttpContext context, ClaimsPrincipal user, ReadingService reading, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!TryParseItemIds(p["i"], MaxItems, out var ids))
        {
            return Results.BadRequest();
        }

        var userId = UserId(user);
        var items = await reading.GetSyncItemsAsync(userId, ids, context.RequestAborted);
        var labels = await LabelsBySubscriptionAsync(userId, library, context.RequestAborted);
        return Results.Json(new
        {
            id = ReadingList,
            updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            items = items.Select(i => ToItem(i, labels)),
        });
    }

    // ---- Writes ------------------------------------------------------------------------------

    private static async Task<IResult> EditTagAsync(HttpContext context, ClaimsPrincipal user, ReadingService reading)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!TryParseItemIds(p["i"], MaxIds, out var ids))
        {
            return Results.BadRequest();
        }

        var userId = UserId(user);
        var add = p["a"].Select(Normalize).ToHashSet();
        var remove = p["r"].Select(Normalize).ToHashSet();
        if (add.Contains(Read) || remove.Contains(Read))
        {
            await reading.SetReadAsync(userId, ids, add.Contains(Read), context.RequestAborted);
        }

        if (add.Contains(Starred) || remove.Contains(Starred))
        {
            await reading.SetSavedAsync(userId, ids, add.Contains(Starred), context.RequestAborted);
        }

        // Other tags (kept-unread, item labels) aren't Skimr concepts; accepting them keeps clients happy.
        return Ok();
    }

    private static async Task<IResult> MarkAllAsReadAsync(HttpContext context, ClaimsPrincipal user, ReadingService reading, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var userId = UserId(user);
        var stream = await ResolveStreamAsync(userId, p["s"].ToString(), library, context.RequestAborted);
        if (stream is null)
        {
            return Results.BadRequest();
        }

        DateTime? before = null;
        if (!StringValues.IsNullOrEmpty(p["ts"]))
        {
            if (!long.TryParse(p["ts"], NumberStyles.None, CultureInfo.InvariantCulture, out var ts) || FromUsec(ts) is not { } at)
            {
                return Results.BadRequest();
            }

            before = at;
        }

        await reading.MarkReadBeforeAsync(userId, stream.Value.View, before, context.RequestAborted);
        return Ok();
    }

    private static async Task<IResult> SubscriptionEditAsync(HttpContext context, ClaimsPrincipal user, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var userId = UserId(user);
        var ct = context.RequestAborted;
        var action = p["ac"].ToString();
        var title = p["t"].ToString().Trim();
        foreach (var s in p["s"])
        {
            var target = s ?? "";
            if (action == "subscribe")
            {
                var url = target.StartsWith(FeedPrefix, StringComparison.Ordinal) ? target[FeedPrefix.Length..] : target;
                var folders = await FolderIdsAsync(userId, p["a"], library, create: true, ct);
                var result = await library.SubscribeAsync(userId, url, folders, ct, title.Length > 0 ? title : null);
                if (!result.Succeeded)
                {
                    return Results.BadRequest(result.Error);
                }

                continue;
            }

            if (await SubscriptionIdAsync(userId, target, library, ct) is not { } id)
            {
                return Results.NotFound();
            }

            if (action == "unsubscribe")
            {
                await library.UnsubscribeAsync(userId, id, ct);
                continue;
            }

            if (action != "edit")
            {
                return Results.BadRequest();
            }

            var current = (await library.ListSourcesAsync(userId, ct)).Single(x => x.SubscriptionId == id);
            var add = await FolderIdsAsync(userId, p["a"], library, create: true, ct);
            var remove = await FolderIdsAsync(userId, p["r"], library, create: false, ct);
            var folderIds = current.FolderIds.Except(remove).Union(add).ToList();
            var result2 = await library.UpdateSourceAsync(
                userId, id, title.Length > 0 ? title : current.CustomTitle, current.RefreshMinutes, folderIds, ct, current.AlwaysFullText);
            if (!result2.Succeeded)
            {
                return Results.BadRequest(result2.Error);
            }
        }

        return Ok();
    }

    private static async Task<IResult> QuickAddAsync(HttpContext context, ClaimsPrincipal user, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var url = p["quickadd"].ToString().Trim();
        if (url.StartsWith(FeedPrefix, StringComparison.Ordinal))
        {
            url = url[FeedPrefix.Length..];
        }

        var result = await library.SubscribeAsync(UserId(user), url, [], context.RequestAborted);
        return result.Succeeded
            ? Results.Json(new { numResults = 1, query = url, streamId = FeedPrefix + result.Id!.Value.ToString(CultureInfo.InvariantCulture), streamName = url })
            : Results.Json(new { numResults = 0, query = url, error = result.Error });
    }

    private static async Task<IResult> RenameTagAsync(HttpContext context, ClaimsPrincipal user, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var userId = UserId(user);
        var from = await FolderIdsAsync(userId, p["s"], library, create: false, context.RequestAborted);
        var to = LabelName(p["dest"].ToString());
        if (from.Count != 1 || to is null)
        {
            return Results.NotFound();
        }

        var result = await library.RenameFolderAsync(userId, from[0], to, context.RequestAborted);
        return result.Succeeded ? Ok() : Results.BadRequest(result.Error);
    }

    private static async Task<IResult> DisableTagAsync(HttpContext context, ClaimsPrincipal user, LibraryService library)
    {
        if (await ParamsAsync(context) is not { } p)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var userId = UserId(user);
        var folders = await FolderIdsAsync(userId, p["s"], library, create: false, context.RequestAborted);
        if (folders.Count == 0)
        {
            return Results.NotFound();
        }

        foreach (var id in folders)
        {
            await library.DeleteFolderAsync(userId, id, context.RequestAborted);
        }

        return Ok();
    }

    // ---- Parsing -----------------------------------------------------------------------------

    internal sealed record StreamQuery(ReaderView View, SyncFilter Filter, ArticleSort Sort, ArticleCursor? After, int Count);

    /// <summary>A stream id plus the filters clients put next to it. Null for malformed input.</summary>
    private static async Task<StreamQuery?> StreamQueryAsync(Guid userId, string stream, IFormCollection p, int max, LibraryService library, CancellationToken ct)
    {
        if (await ResolveStreamAsync(userId, stream, library, ct) is not { } resolved)
        {
            return null;
        }

        var filter = resolved.Filter;
        foreach (var exclude in p["xt"].Select(Normalize))
        {
            filter = exclude switch
            {
                Read => filter with { Read = false },
                Starred => filter with { Saved = false },
                _ => filter,
            };
        }

        foreach (var include in p["it"].Select(Normalize))
        {
            filter = include switch
            {
                Read => filter with { Read = true },
                Starred => filter with { Saved = true },
                _ => filter,
            };
        }

        if (!TryUnixSeconds(p["ot"], out var since) || !TryUnixSeconds(p["nt"], out var until) || !ApiCursor.TryDecode(p["c"].ToString(), out var after))
        {
            return null;
        }

        var count = 20;
        if (!StringValues.IsNullOrEmpty(p["n"]))
        {
            if (!int.TryParse(p["n"], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1)
            {
                return null;
            }
        }

        return new StreamQuery(
            resolved.View,
            filter with { Since = since ?? filter.Since, Until = until ?? filter.Until },
            p["r"] == "o" ? ArticleSort.Oldest : ArticleSort.Newest,
            after,
            Math.Min(count, max));
    }

    /// <summary>Maps a stream id to a view. Unknown labels and feeds give an empty view, as Google did.</summary>
    private static async Task<(ReaderView View, SyncFilter Filter)?> ResolveStreamAsync(Guid userId, string stream, LibraryService library, CancellationToken ct)
    {
        var s = Normalize(stream);
        if (s.Length == 0 || s == ReadingList)
        {
            return (ReaderView.All, new SyncFilter());
        }

        if (s == Starred)
        {
            return (ReaderView.Saved, new SyncFilter());
        }

        if (s == Read)
        {
            return (ReaderView.All, new SyncFilter(Read: true));
        }

        if (s.StartsWith(LabelPrefix, StringComparison.Ordinal))
        {
            var folders = await FolderIdsAsync(userId, new StringValues(s), library, create: false, ct);
            return (ReaderView.Folder(folders.Count == 1 ? folders[0] : -1), new SyncFilter());
        }

        if (s.StartsWith(FeedPrefix, StringComparison.Ordinal))
        {
            return (ReaderView.Source(await SubscriptionIdAsync(userId, s, library, ct) ?? -1), new SyncFilter());
        }

        if (s.StartsWith("user/-/state/", StringComparison.Ordinal))
        {
            // Other Google states (broadcast, like, kept-unread…) have nothing in them here.
            return (ReaderView.Folder(-1), new SyncFilter());
        }

        return null;
    }

    /// <summary>"feed/12" is a subscription id; clients that only know the URL send "feed/https://…".</summary>
    private static async Task<long?> SubscriptionIdAsync(Guid userId, string stream, LibraryService library, CancellationToken ct)
    {
        var rest = stream.StartsWith(FeedPrefix, StringComparison.Ordinal) ? stream[FeedPrefix.Length..] : stream;
        var sources = await library.ListSourcesAsync(userId, ct);
        return long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? sources.FirstOrDefault(x => x.SubscriptionId == id)?.SubscriptionId
            : sources.FirstOrDefault(x => string.Equals(x.FeedUrl, rest, StringComparison.Ordinal))?.SubscriptionId;
    }

    /// <summary>The user's folder ids for label stream ids, optionally creating missing ones.</summary>
    private static async Task<List<long>> FolderIdsAsync(Guid userId, StringValues labels, LibraryService library, bool create, CancellationToken ct)
    {
        var names = labels.Select(l => LabelName(l ?? "")).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return [];
        }

        var existing = (await library.ListFoldersAsync(userId, ct)).ToList();
        var ids = new List<long>();
        foreach (var name in names)
        {
            if (existing.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal)) is { } folder)
            {
                ids.Add(folder.Id);
            }
            else if (create && (await library.CreateFolderAsync(userId, name, ct)) is { Succeeded: true, Id: { } id })
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string? LabelName(string stream)
    {
        var s = Normalize(stream);
        return s.StartsWith(LabelPrefix, StringComparison.Ordinal) && s.Length > LabelPrefix.Length ? s[LabelPrefix.Length..] : null;
    }

    /// <summary>"user/1234/…" and "user/-/…" mean the same thing.</summary>
    internal static string Normalize(string? stream) => UserPrefix().Replace((stream ?? "").Trim(), "user/-/", 1);

    [GeneratedRegex(@"^user/[^/]+/")]
    private static partial Regex UserPrefix();

    /// <summary>Accepts "tag:google.com,2005:reader/item/000000000000001f" and plain decimal "31".</summary>
    internal static bool TryParseItemId(string? text, out long id)
    {
        id = 0;
        var s = (text ?? "").Trim();
        if (s.StartsWith(LongIdPrefix, StringComparison.Ordinal))
        {
            var hex = s[LongIdPrefix.Length..];
            if (hex.Length is > 0 and <= 16 && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u))
            {
                id = unchecked((long)u);
                return id > 0;
            }

            return false;
        }

        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static bool TryParseItemIds(StringValues values, int max, out List<long> ids)
    {
        ids = [];
        foreach (var value in values)
        {
            if (!TryParseItemId(value, out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        ids = ids.Distinct().ToList();
        return ids.Count <= max;
    }

    internal static string LongItemId(long id) => LongIdPrefix + id.ToString("x16", CultureInfo.InvariantCulture);

    private static bool TryUnixSeconds(StringValues value, out DateTime? at)
    {
        at = null;
        if (StringValues.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 253_402_300_799)
        {
            return false;
        }

        at = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return true;
    }

    internal static long Usec(DateTime at) => (DateTime.SpecifyKind(at, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks / 10;

    private static DateTime? FromUsec(long usec) =>
        usec is >= 0 and <= 253_402_300_799_999_999 ? DateTime.UnixEpoch.AddTicks(usec * 10) : null;

    /// <summary>Query string plus form body (clients use both), capped at 1 MB. Null if larger.</summary>
    private static async Task<IFormCollection?> ParamsAsync(HttpContext context)
    {
        var request = context.Request;
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Query)
        {
            values[key] = value;
        }

        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = MaxFormBytes;
            }

            if (request.ContentLength > MaxFormBytes)
            {
                return null;
            }

            // Edit-tag and contents can repeat "i" thousands of times; the default allows 1,024 values.
            var form = await new FormFeature(request, new Microsoft.AspNetCore.Http.Features.FormOptions { ValueCountLimit = MaxIds + 100 })
                .ReadFormAsync(context.RequestAborted);
            foreach (var (key, value) in form)
            {
                values[key] = values.TryGetValue(key, out var existing) ? StringValues.Concat(existing, value) : value;
            }
        }

        return new FormCollection(values);
    }

    private static async Task<Dictionary<long, List<string>>> LabelsBySubscriptionAsync(Guid userId, LibraryService library, CancellationToken ct)
    {
        var folders = (await library.ListFoldersAsync(userId, ct)).ToDictionary(f => f.Id, f => f.Name);
        return (await library.ListSourcesAsync(userId, ct)).ToDictionary(
            s => s.SubscriptionId,
            s => s.FolderIds.Where(folders.ContainsKey).Select(f => LabelPrefix + folders[f]).ToList());
    }

    private static object ToItem(SyncItem item, Dictionary<long, List<string>> labels)
    {
        var categories = new List<string> { ReadingList };
        if (item.IsRead)
        {
            categories.Add(Read);
        }

        if (item.IsSaved)
        {
            categories.Add(Starred);
        }

        if (item.SubscriptionId is { } sub && labels.TryGetValue(sub, out var mine))
        {
            categories.AddRange(mine);
        }

        var published = new DateTimeOffset(DateTime.SpecifyKind(item.PublishedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var link = item.Url is null ? Array.Empty<object>() : [new { href = item.Url, type = "text/html" }];
        return new
        {
            id = LongItemId(item.ArticleId),
            crawlTimeMsec = (Usec(item.FetchedAt) / 1000).ToString(CultureInfo.InvariantCulture),
            timestampUsec = Usec(item.PublishedAt).ToString(CultureInfo.InvariantCulture),
            published,
            updated = published,
            title = item.Title,
            author = item.Author ?? "",
            canonical = link,
            alternate = link,
            categories,
            origin = new
            {
                streamId = item.SubscriptionId is { } id ? FeedPrefix + id.ToString(CultureInfo.InvariantCulture) : "",
                title = item.SourceTitle,
                htmlUrl = item.SiteUrl ?? "",
            },
            // Stored HTML is already sanitised and has the original image URLs: API clients can't
            // use the cookie-bound image proxy (PLAN D54).
            summary = new { direction = "ltr", content = item.ContentHtml },
        };
    }

    private static IResult Ok() => Results.Text("OK", "text/plain");

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
