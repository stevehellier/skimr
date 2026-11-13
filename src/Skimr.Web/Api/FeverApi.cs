using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.Web.Auth;

namespace Skimr.Web.Api;

/// <summary>
/// Fever API (https://feedafever.com/api, version 3) at /api/fever, for Reeder, ReadKit, Unread
/// and friends (PLAN D61). The api_key is md5("UserName:token") for an API token created since
/// Fever support. Fever clients expect HTTP 200 for everything: a bad key is <c>"auth": 0</c>.
/// Groups are folders, feeds are subscriptions, "saved" is Read Later; there are no sparks.
/// </summary>
internal static class FeverApi
{
    public const string Path = "/api/fever";
    public const int MaxItems = 50;

    public static IEndpointRouteBuilder MapFeverApi(this IEndpointRouteBuilder app)
    {
        // Anonymous by design: the api_key in the body is the credential, checked below.
        app.MapMethods(Path, ["GET", "POST"], HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(Policies.ApiRateLimit)
            .DisableAntiforgery()
            .WithMetadata(new SkipStatusCodePagesAttribute())
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context, ApiTokenService tokens, UserManager<Infrastructure.Identity.SkimrUser> users,
        ReadingService reading, LibraryService library, SkimrDbContext db, StorageOptions storage)
    {
        var ct = context.RequestAborted;
        var response = new Dictionary<string, object> { ["api_version"] = 3, ["auth"] = 0 };
        var p = await GoogleReaderApi.ParamsAsync(context);
        var key = p?["api_key"].ToString() ?? "";
        var token = key.Length == 0 ? null : await tokens.ValidateFeverKeyAsync(key, ct);
        var user = token is null ? null : await users.FindByIdAsync(token.Value.UserId.ToString());
        if (p is null || user is null || AccountService.IsDisabled(user))
        {
            return Results.Json(response);
        }

        var userId = user.Id;
        response["auth"] = 1;
        var sources = await library.ListSourcesAsync(userId, ct);
        response["last_refreshed_on_time"] = Unix(sources.Max(s => s.LastSuccessAt));

        // Writes first, so the id lists below reflect them (clients read them back after a mark).
        if (p.ContainsKey("mark"))
        {
            await MarkAsync(p, userId, reading, sources, ct);
        }

        if (p.ContainsKey("groups") || p.ContainsKey("feeds"))
        {
            var folders = await library.ListFoldersAsync(userId, ct);
            if (p.ContainsKey("groups"))
            {
                response["groups"] = folders.Select(f => new { id = f.Id, title = f.Name });
            }

            if (p.ContainsKey("feeds"))
            {
                response["feeds"] = sources.Select(s => new
                {
                    id = s.SubscriptionId,
                    favicon_id = s.IconId ?? 0,
                    title = s.Title,
                    url = s.FeedUrl,
                    site_url = s.SiteUrl ?? "",
                    is_spark = 0,
                    last_updated_on_time = Unix(s.LastSuccessAt),
                });
            }

            response["feeds_groups"] = folders.Select(f => new
            {
                group_id = f.Id,
                feed_ids = string.Join(',', sources.Where(s => s.FolderIds.Contains(f.Id)).Select(s => s.SubscriptionId)),
            });
        }

        if (p.ContainsKey("favicons"))
        {
            response["favicons"] = await FaviconsAsync(sources, db, storage, ct);
        }

        if (p.ContainsKey("items"))
        {
            response["items"] = await ItemsAsync(p, userId, reading, ct);
            response["total_items"] = await reading.CountAsync(userId, ReaderView.All, ct);
        }

        if (p.ContainsKey("links"))
        {
            response["links"] = Array.Empty<object>();
        }

        if (p.ContainsKey("unread_item_ids") || (p.ContainsKey("mark") && p["as"].ToString() is "read" or "unread"))
        {
            response["unread_item_ids"] = string.Join(',', await reading.GetAllArticleIdsAsync(userId, new SyncFilter(Read: false), ct));
        }

        if (p.ContainsKey("saved_item_ids") || (p.ContainsKey("mark") && p["as"].ToString() is "saved" or "unsaved"))
        {
            response["saved_item_ids"] = string.Join(',', await reading.GetAllArticleIdsAsync(userId, new SyncFilter(Saved: true), ct));
        }

        return Results.Json(response);
    }

    private static async Task<IEnumerable<object>> ItemsAsync(IFormCollection p, Guid userId, ReadingService reading, CancellationToken ct)
    {
        IReadOnlyList<long> ids;
        if (p.ContainsKey("with_ids"))
        {
            ids = p["with_ids"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .Take(MaxItems)
                .ToList();
        }
        else
        {
            ids = await reading.GetArticleIdsByIdAsync(userId, Long(p["since_id"]), Long(p["max_id"]), MaxItems, ct);
        }

        var items = await reading.GetSyncItemsAsync(userId, ids, ct);
        // Clients page with since_id and max_id, so items go in id order.
        return items.OrderBy(i => i.ArticleId).Select(i => new
        {
            id = i.ArticleId,
            feed_id = i.SubscriptionId ?? 0,
            title = i.Title,
            author = i.Author ?? "",
            // Stored HTML: sanitised, with original image URLs (API clients can't use the proxy, D54).
            html = i.ContentHtml,
            url = i.Url ?? "",
            is_saved = i.IsSaved ? 1 : 0,
            is_read = i.IsRead ? 1 : 0,
            created_on_time = Unix(i.PublishedAt),
        });
    }

    private static async Task MarkAsync(IFormCollection p, Guid userId, ReadingService reading, IReadOnlyList<SourceDetails> sources, CancellationToken ct)
    {
        var id = Long(p["id"]);
        if (id is null)
        {
            return;
        }

        var before = Long(p["before"]) is { } seconds and > 0 and < 253_402_300_800 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : (DateTime?)null;
        switch (p["mark"].ToString(), p["as"].ToString())
        {
            case ("item", "read"):
                await reading.SetReadAsync(userId, [id.Value], true, ct);
                break;
            case ("item", "unread"):
                await reading.SetReadAsync(userId, [id.Value], false, ct);
                break;
            case ("item", "saved"):
                await reading.SetSavedAsync(userId, [id.Value], true, ct);
                break;
            case ("item", "unsaved"):
                await reading.SetSavedAsync(userId, [id.Value], false, ct);
                break;
            case ("feed", "read") when sources.Any(s => s.SubscriptionId == id):
                await reading.MarkReadBeforeAsync(userId, ReaderView.Source(id.Value), before, ct);
                break;
            case ("group", "read") when id == 0:
                // Group 0 is "Kindling": every feed.
                await reading.MarkReadBeforeAsync(userId, ReaderView.All, before, ct);
                break;
            case ("group", "read") when id > 0:
                // Folder ids from the client go through the service's own ownership check.
                await reading.MarkReadBeforeAsync(userId, ReaderView.Folder(id.Value), before, ct);
                break;
        }
    }

    /// <summary>Raster icons only, as "image/png;base64,…" (the spec's form; clients add "data:").</summary>
    private static async Task<List<object>> FaviconsAsync(IReadOnlyList<SourceDetails> sources, SkimrDbContext db, StorageOptions storage, CancellationToken ct)
    {
        var iconIds = sources.Select(s => s.IconId).OfType<long>().Distinct().ToList();
        var icons = await db.Icons.Where(i => iconIds.Contains(i.Id) && i.MimeType != "image/svg+xml").AsNoTracking().ToListAsync(ct);
        var result = new List<object>();
        foreach (var icon in icons)
        {
            var path = System.IO.Path.Combine(storage.IconDirectory, icon.FileName);
            if (File.Exists(path))
            {
                result.Add(new { id = icon.Id, data = icon.MimeType + ";base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)) });
            }
        }

        return result;
    }

    private static long? Long(StringValues value) =>
        long.TryParse(value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long Unix(DateTime? at) =>
        at is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
}
