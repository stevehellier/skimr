using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.Web.Auth;

namespace Skimr.Web.Api;

public sealed record TokenRequest(string? Name, int? ExpiresInDays);

public sealed record DiscoverRequest(string Url);

public sealed record SubscribeRequest(string Url, long[]? FolderIds);

/// <summary>Null leaves a field as it is. An empty customTitle clears it; refreshMinutes 0 means the default.</summary>
public sealed record SourcePatch(string? CustomTitle, int? RefreshMinutes, long[]? FolderIds, bool? AlwaysFullText);

public sealed record MoveRequest(string Direction, long? FolderId);

public sealed record NameRequest(string Name);

public sealed record ArticlePatch(bool? Read, bool? Saved);

public sealed record ArticleBatchPatch(long[] Ids, bool? Read, bool? Saved);

public sealed record MarkReadRequest(string View, int? OlderThanDays);

public sealed record SettingsPatch(MarkReadMode? MarkReadMode);

public sealed record ViewPreferenceRequest(ArticleLayout Layout, ArticleSort Sort, bool UnreadOnly);

public sealed record CreateUserRequest(string UserName, string Password, bool IsAdmin);

public sealed record UserPatch(bool? Disabled, bool? IsAdmin, string? Password);

public sealed record InviteRequest(string? Note);

public sealed record AdminSettingsPatch(RegistrationMode? RegistrationMode);

public sealed record ArticlePageResponse(IReadOnlyList<ArticleListItem> Items, string? Next);

public sealed record FeedHealth(long Id, string Url, string? Title, int Subscribers, DateTime? LastSuccessAt, DateTime? LastErrorAt, string? LastError, int ConsecutiveFailures, DateTime NextFetchAt);

/// <summary>
/// REST API under /api/v1: everything the UI can do (PLAN §3). Bearer tokens only (D33); every
/// id from the client is resolved against the token's user by the services.
/// </summary>
internal static class RestApi
{
    public const int MaxPageSize = 100;
    public const int MaxBatch = 1000;

    public static IEndpointRouteBuilder MapRestApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(Policies.Api)
            .RequireRateLimiting(Policies.ApiRateLimit)
            .DisableAntiforgery()
            .WithMetadata(new SkipStatusCodePagesAttribute())
            .WithTags("Skimr");

        MapAccount(api);
        MapSources(api);
        MapFolders(api);
        MapArticles(api);
        MapBoards(api);
        MapSettings(api);
        MapAdmin(api.MapGroup("/admin").RequireAuthorization(Policies.Admin).WithTags("Admin"));
        return app;
    }

    private static Guid Uid(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult Outcome(LibraryResult result, Func<IResult>? ok = null) =>
        result.Succeeded ? ok?.Invoke() ?? Results.NoContent()
        : result.IsNotFound ? Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound)
        : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadRequest(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryDirection(string? text, out MoveDirection direction) =>
        Enum.TryParse(text, ignoreCase: true, out direction) && Enum.IsDefined(direction);

    // ---- Me and tokens ---------------------------------------------------------------------

    private static void MapAccount(RouteGroupBuilder api)
    {
        api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { id = Uid(user), userName = user.Identity?.Name, isAdmin = user.IsInRole(SkimrUser.AdminRole) }))
            .WithName("GetMe");

        api.MapGet("/tokens", async (ClaimsPrincipal user, ApiTokenService tokens, CancellationToken ct) =>
            (await tokens.ListAsync(Uid(user), ct)).Select(t => new { t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, feverEnabled = t.FeverKeyHash != null }))
            .WithName("ListTokens");

        api.MapPost("/tokens", async (TokenRequest request, ClaimsPrincipal user, ApiTokenService tokens, CancellationToken ct) =>
        {
            if (request.ExpiresInDays is < 1 or > 3650)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["expiresInDays"] = ["Must be between 1 and 3650, or omitted."] });
            }

            var created = await tokens.CreateAsync(Uid(user), request.Name ?? "API token", request.ExpiresInDays, ct);
            var t = created.Token;
            return Results.Created($"/api/v1/tokens/{t.Id}", new { t.Id, t.Name, t.Prefix, t.CreatedAt, t.ExpiresAt, token = created.PlainText });
        }).WithName("CreateToken");

        api.MapDelete("/tokens/{id:long}", async (long id, ClaimsPrincipal user, ApiTokenService tokens, CancellationToken ct) =>
            await tokens.RevokeAsync(Uid(user), id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("RevokeToken");
    }

    // ---- Sources and discovery --------------------------------------------------------------

    private static void MapSources(RouteGroupBuilder api)
    {
        api.MapPost("/discover", async (DiscoverRequest request, LibraryService library, CancellationToken ct) =>
        {
            var (found, error) = await library.DiscoverAsync(request.Url ?? "", ct);
            return found.Count == 0 ? BadRequest(error ?? "No feeds found.") : Results.Ok(found);
        }).WithName("DiscoverFeeds");

        api.MapGet("/subscriptions", async (ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            Results.Ok(await library.ListSourcesAsync(Uid(user), ct)))
            .WithName("ListSubscriptions").Produces<IReadOnlyList<SourceDetails>>();

        api.MapPost("/subscriptions", async (SubscribeRequest request, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
        {
            var result = await library.SubscribeAsync(Uid(user), request.Url ?? "", request.FolderIds ?? [], ct);
            return Outcome(result, () => Results.Created($"/api/v1/subscriptions/{result.Id}", new { id = result.Id }));
        }).WithName("Subscribe");

        api.MapGet("/subscriptions/{id:long}", async (long id, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            (await library.ListSourcesAsync(Uid(user), ct)).FirstOrDefault(s => s.SubscriptionId == id) is { } source ? Results.Ok(source) : Results.NotFound())
            .WithName("GetSubscription").Produces<SourceDetails>();

        api.MapPatch("/subscriptions/{id:long}", async (long id, SourcePatch patch, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
        {
            var userId = Uid(user);
            if ((await library.ListSourcesAsync(userId, ct)).FirstOrDefault(s => s.SubscriptionId == id) is not { } current)
            {
                return Results.NotFound();
            }

            var minutes = patch.RefreshMinutes switch { null => current.RefreshMinutes, 0 => null, var m => m };
            return Outcome(await library.UpdateSourceAsync(
                userId,
                id,
                patch.CustomTitle ?? current.CustomTitle,
                minutes,
                patch.FolderIds ?? [.. current.FolderIds],
                ct,
                patch.AlwaysFullText ?? current.AlwaysFullText));
        }).WithName("UpdateSubscription");

        api.MapDelete("/subscriptions/{id:long}", async (long id, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            Outcome(await library.UnsubscribeAsync(Uid(user), id, ct)))
            .WithName("Unsubscribe");

        api.MapPost("/subscriptions/{id:long}/refresh", async (long id, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            Outcome(await library.RefreshNowAsync(Uid(user), id, ct), () => Results.Accepted()))
            .WithName("RefreshSubscription");

        api.MapPost("/subscriptions/{id:long}/move", async (long id, MoveRequest request, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            TryDirection(request.Direction, out var direction)
                ? Outcome(await library.MoveSourceAsync(Uid(user), request.FolderId, id, direction, ct))
                : BadRequest("direction must be 'up' or 'down'."))
            .WithName("MoveSubscription");

        api.MapGet("/opml", async (ClaimsPrincipal user, OpmlService opml, CancellationToken ct) =>
            Results.Text(await opml.ExportAsync(Uid(user), ct), "text/x-opml"))
            .WithName("ExportOpml");

        api.MapPost("/opml", async (HttpContext context, ClaimsPrincipal user, OpmlService opml, CancellationToken ct) =>
        {
            if (await ReadBodyAsync(context, OpmlService.MaxUploadBytes, ct) is not { } body)
            {
                return Results.Problem("OPML files are limited to 2 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            return Results.Ok(await opml.ImportAsync(Uid(user), body, ct));
        }).WithName("ImportOpml").Accepts<string>("text/x-opml", "application/xml", "text/xml").Produces<OpmlImportResult>();
    }

    // ---- Folders -----------------------------------------------------------------------------

    private static void MapFolders(RouteGroupBuilder api)
    {
        api.MapGet("/folders", async (ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            Results.Ok(await library.ListFoldersAsync(Uid(user), ct)))
            .WithName("ListFolders").Produces<IReadOnlyList<FolderDetails>>();

        api.MapPost("/folders", async (NameRequest request, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
        {
            var result = await library.CreateFolderAsync(Uid(user), request.Name ?? "", ct);
            return Outcome(result, () => Results.Created($"/api/v1/folders/{result.Id}", new { id = result.Id }));
        }).WithName("CreateFolder");

        api.MapPatch("/folders/{id:long}", async (long id, NameRequest request, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            Outcome(await library.RenameFolderAsync(Uid(user), id, request.Name ?? "", ct)))
            .WithName("RenameFolder");

        api.MapDelete("/folders/{id:long}", async (long id, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            Outcome(await library.DeleteFolderAsync(Uid(user), id, ct)))
            .WithName("DeleteFolder");

        api.MapPost("/folders/{id:long}/move", async (long id, MoveRequest request, ClaimsPrincipal user, LibraryService library, CancellationToken ct) =>
            TryDirection(request.Direction, out var direction)
                ? Outcome(await library.MoveFolderAsync(Uid(user), id, direction, ct))
                : BadRequest("direction must be 'up' or 'down'."))
            .WithName("MoveFolder");
    }

    // ---- Articles, counts and search ---------------------------------------------------------

    private static void MapArticles(RouteGroupBuilder api)
    {
        api.MapGet("/counts", async (ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
            Results.Ok(await reading.GetNavAsync(Uid(user), ct)))
            .WithName("GetCounts").Produces<NavModel>();

        api.MapGet("/articles", async (ClaimsPrincipal user, ReadingService reading, CancellationToken ct,
            string? view, bool? unreadOnly, ArticleSort? sort, string? cursor, int? limit) =>
        {
            if (!ReaderView.TryParse(view ?? "all", out var parsed))
            {
                return BadRequest("view must be all, today, saved, folder:{id}, source:{id} or board:{id}.");
            }

            if (!ApiCursor.TryDecode(cursor, out var after))
            {
                return BadRequest("cursor is not valid; pass back the 'next' value unchanged.");
            }

            var page = await reading.GetArticlesAsync(Uid(user), parsed, sort ?? ArticleSort.Newest, unreadOnly ?? false, after, ct, Math.Clamp(limit ?? ReadingService.PageSize, 1, MaxPageSize));
            return Results.Ok(new ArticlePageResponse(page.Items, ApiCursor.Encode(page.Next)));
        }).WithName("ListArticles").Produces<ArticlePageResponse>();

        api.MapGet("/articles/{id:long}", async (long id, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
            await reading.GetArticleAsync(Uid(user), id, ct) is { } article ? Results.Ok(article) : Results.NotFound())
            .WithName("GetArticle").Produces<ArticleDetail>();

        api.MapPatch("/articles/{id:long}", async (long id, ArticlePatch patch, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
        {
            var userId = Uid(user);
            if (await reading.GetArticleAsync(userId, id, ct) is null)
            {
                return Results.NotFound();
            }

            if (patch.Read is { } read)
            {
                await reading.SetReadAsync(userId, [id], read, ct);
            }

            if (patch.Saved is { } saved)
            {
                await reading.SetSavedAsync(userId, id, saved, ct);
            }

            return Results.NoContent();
        }).WithName("UpdateArticle");

        api.MapPatch("/articles", async (ArticleBatchPatch patch, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
        {
            var ids = (patch.Ids ?? []).Distinct().ToList();
            if (ids.Count > MaxBatch)
            {
                return BadRequest($"At most {MaxBatch} ids at a time.");
            }

            var userId = Uid(user);
            var changed = 0;
            if (patch.Read is { } read)
            {
                changed = await reading.SetReadAsync(userId, ids, read, ct);
            }

            if (patch.Saved is { } saved)
            {
                changed += await reading.SetSavedAsync(userId, ids, saved, ct);
            }

            return Results.Ok(new { changed });
        }).WithName("UpdateArticles");

        api.MapPost("/articles/mark-read", async (MarkReadRequest request, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
        {
            if (!ReaderView.TryParse(request.View, out var view))
            {
                return BadRequest("view must be all, today, folder:{id} or source:{id}.");
            }

            if (request.OlderThanDays is < 0 or > 3650)
            {
                return BadRequest("olderThanDays must be between 0 and 3650.");
            }

            TimeSpan? age = request.OlderThanDays is { } days and > 0 ? TimeSpan.FromDays(days) : null;
            return Results.Ok(new { marked = await reading.MarkAllReadAsync(Uid(user), view, age, ct) });
        }).WithName("MarkAllRead");

        api.MapPost("/articles/{id:long}/full-text", async (long id, ClaimsPrincipal user, FullTextService fullText, CancellationToken ct) =>
        {
            var result = await fullText.GetAsync(Uid(user), id, ct);
            return result.Html is not null ? Results.Ok(new { html = result.Html })
                : result.Error == "Article not found." ? Results.NotFound()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status502BadGateway);
        }).WithName("LoadFullText");

        api.MapGet("/search", async (ClaimsPrincipal user, ReadingService reading, CancellationToken ct,
            string q, long? source, long? folder, DateTime? from, DateTime? to, ReadFilter? read, string? cursor, int? limit) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
            {
                return BadRequest("q is required and at most 200 characters.");
            }

            if (!ApiCursor.TryDecode(cursor, out var after))
            {
                return BadRequest("cursor is not valid; pass back the 'next' value unchanged.");
            }

            var page = await reading.SearchAsync(
                Uid(user), new SearchQuery(q, source, folder, from?.ToUniversalTime(), to?.ToUniversalTime(), read ?? ReadFilter.Any), after, ct, Math.Clamp(limit ?? ReadingService.PageSize, 1, MaxPageSize));
            return Results.Ok(new ArticlePageResponse(page.Items, ApiCursor.Encode(page.Next)));
        }).WithName("Search").Produces<ArticlePageResponse>();
    }

    // ---- Boards ------------------------------------------------------------------------------

    private static void MapBoards(RouteGroupBuilder api)
    {
        api.MapGet("/boards", async (ClaimsPrincipal user, BoardService boards, CancellationToken ct) =>
            Results.Ok(await boards.ListAsync(Uid(user), ct)))
            .WithName("ListBoards").Produces<IReadOnlyList<BoardDetails>>();

        api.MapPost("/boards", async (NameRequest request, ClaimsPrincipal user, BoardService boards, CancellationToken ct) =>
        {
            var result = await boards.CreateAsync(Uid(user), request.Name ?? "", ct);
            return Outcome(result, () => Results.Created($"/api/v1/boards/{result.Id}", new { id = result.Id }));
        }).WithName("CreateBoard");

        api.MapPatch("/boards/{id:long}", async (long id, NameRequest request, ClaimsPrincipal user, BoardService boards, CancellationToken ct) =>
            Outcome(await boards.RenameAsync(Uid(user), id, request.Name ?? "", ct)))
            .WithName("RenameBoard");

        api.MapDelete("/boards/{id:long}", async (long id, ClaimsPrincipal user, BoardService boards, CancellationToken ct) =>
            Outcome(await boards.DeleteAsync(Uid(user), id, ct)))
            .WithName("DeleteBoard");

        api.MapPut("/boards/{id:long}/articles/{articleId:long}", async (long id, long articleId, ClaimsPrincipal user, BoardService boards, CancellationToken ct) =>
            Outcome(await boards.AddAsync(Uid(user), id, articleId, ct)))
            .WithName("AddToBoard");

        api.MapDelete("/boards/{id:long}/articles/{articleId:long}", async (long id, long articleId, ClaimsPrincipal user, BoardService boards, CancellationToken ct) =>
            Outcome(await boards.RemoveAsync(Uid(user), id, articleId, ct)))
            .WithName("RemoveFromBoard");
    }

    // ---- Settings and per-view preferences ---------------------------------------------------

    private static void MapSettings(RouteGroupBuilder api)
    {
        api.MapGet("/settings", async (ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
            Results.Ok(new { markReadMode = await reading.GetMarkReadModeAsync(Uid(user), ct) }))
            .WithName("GetSettings");

        api.MapPatch("/settings", async (SettingsPatch patch, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
        {
            if (patch.MarkReadMode is { } mode)
            {
                await reading.SetMarkReadModeAsync(Uid(user), mode, ct);
            }

            return Results.NoContent();
        }).WithName("UpdateSettings");

        api.MapGet("/views/{key}", async (string key, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
            ReaderView.TryParse(key, out var view)
                ? Results.Ok(await reading.GetViewPreferenceAsync(Uid(user), view, ct))
                : BadRequest("Unknown view."))
            .WithName("GetViewPreference");

        api.MapPut("/views/{key}", async (string key, ViewPreferenceRequest request, ClaimsPrincipal user, ReadingService reading, CancellationToken ct) =>
        {
            if (!ReaderView.TryParse(key, out var view))
            {
                return BadRequest("Unknown view.");
            }

            await reading.SaveViewPreferenceAsync(Uid(user), view, request.Layout, request.Sort, request.UnreadOnly, ct);
            return Results.NoContent();
        }).WithName("SaveViewPreference");
    }

    // ---- Admin -------------------------------------------------------------------------------

    private static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("/users", async (AccountService accounts, CancellationToken ct) => Results.Ok(await accounts.ListUsersAsync(ct)))
            .WithName("ListUsers");

        admin.MapPost("/users", async (CreateUserRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.CreateUserAsync(request.UserName ?? "", request.Password ?? "", request.IsAdmin, ct);
            return result.Succeeded
                ? Results.Created($"/api/v1/admin/users/{result.UserId}", new { id = result.UserId })
                : BadRequest(string.Join(" ", result.Errors));
        }).WithName("CreateUser");

        admin.MapPatch("/users/{id:guid}", async (Guid id, UserPatch patch, ClaimsPrincipal user, AccountService accounts, CancellationToken ct) =>
        {
            var actor = Uid(user);
            var results = new List<AccountResult>();
            if (patch.Disabled is { } disabled)
            {
                results.Add(await accounts.SetDisabledAsync(actor, id, disabled, ct));
            }

            if (patch.IsAdmin is { } isAdmin)
            {
                results.Add(await accounts.SetAdminAsync(actor, id, isAdmin, ct));
            }

            if (patch.Password is { } password)
            {
                results.Add(await accounts.ResetPasswordAsync(id, password));
            }

            var failed = results.FirstOrDefault(r => !r.Succeeded);
            return failed is null ? Results.NoContent()
                : failed.Errors.Any(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase)) ? Results.NotFound()
                : BadRequest(string.Join(" ", failed.Errors));
        }).WithName("UpdateUser");

        admin.MapDelete("/users/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.DeleteAsync(Uid(user), id, ct);
            return result.Succeeded ? Results.NoContent()
                : result.Errors.Any(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase)) ? Results.NotFound()
                : BadRequest(string.Join(" ", result.Errors));
        }).WithName("DeleteUser");

        admin.MapGet("/invites", async (AccountService accounts, CancellationToken ct) =>
            Results.Ok((await accounts.ListPendingInvitesAsync(ct)).Select(i => new { i.Id, i.Note, i.CreatedAt, i.ExpiresAt })))
            .WithName("ListInvites");

        admin.MapPost("/invites", async (InviteRequest request, ClaimsPrincipal user, AccountService accounts, CancellationToken ct) =>
            Results.Ok(new { code = await accounts.CreateInviteAsync(Uid(user), request.Note, ct) }))
            .WithName("CreateInvite");

        admin.MapDelete("/invites/{id:long}", async (long id, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.RevokeInviteAsync(id, ct);
            return Results.NoContent();
        }).WithName("RevokeInvite");

        admin.MapGet("/settings", async (AccountService accounts, CancellationToken ct) =>
            Results.Ok(new { registrationMode = await accounts.GetRegistrationModeAsync(ct) }))
            .WithName("GetAdminSettings");

        admin.MapPatch("/settings", async (AdminSettingsPatch patch, AccountService accounts, CancellationToken ct) =>
        {
            if (patch.RegistrationMode is { } mode)
            {
                await accounts.SetRegistrationModeAsync(mode, ct);
            }

            return Results.NoContent();
        }).WithName("UpdateAdminSettings");

        admin.MapGet("/feeds", async (SkimrDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Feeds
                .OrderByDescending(f => f.ConsecutiveFailures).ThenBy(f => f.Url)
                .Select(f => new FeedHealth(f.Id, f.Url, f.Title, f.Subscriptions.Count, f.LastSuccessAt, f.LastErrorAt, f.LastError, f.ConsecutiveFailures, f.NextFetchAt))
                .AsNoTracking()
                .ToListAsync(ct)))
            .WithName("ListFeedHealth").Produces<List<FeedHealth>>();
    }

    /// <summary>Reads the raw request body up to <paramref name="limit"/> bytes; null if it's larger.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(HttpContext context, long limit, CancellationToken ct)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit;
        }

        if (context.Request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
