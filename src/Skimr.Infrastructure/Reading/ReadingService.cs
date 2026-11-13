using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record NavSource(long SubscriptionId, long FeedId, string Title, int Unread, bool HasError, long? IconId = null);

public sealed record NavFolder(long Id, string Name, int Unread, IReadOnlyList<NavSource> Sources);

public sealed record NavBoard(long Id, string Name, int Count);

public sealed record NavModel(int AllUnread, int TodayUnread, int SavedCount, IReadOnlyList<NavFolder> Folders, IReadOnlyList<NavSource> Unfiled, IReadOnlyList<NavBoard>? Boards = null)
{
    public static readonly NavModel Empty = new(0, 0, 0, [], []);

    public IReadOnlyList<NavBoard> BoardList => Boards ?? [];
}

public enum ReadFilter
{
    Any,
    Unread,
    Read,
}

/// <summary>Search text plus optional filters. Source and folder ids are checked against the user.</summary>
public sealed record SearchQuery(string Text, long? SourceId = null, long? FolderId = null, DateTime? From = null, DateTime? To = null, ReadFilter Read = ReadFilter.Any);

public sealed record ArticleCursor(DateTime PublishedAt, long ArticleId);

public sealed record ArticleListItem(
    long ArticleId,
    string SourceTitle,
    string Title,
    string? Snippet,
    string? ThumbnailUrl,
    string? Url,
    string? Author,
    DateTime PublishedAt,
    bool IsRead,
    bool IsSaved);

public sealed record ArticlePage(IReadOnlyList<ArticleListItem> Items, ArticleCursor? Next);

/// <summary>Sync filters (Google Reader, Fever). Null means "either".</summary>
public sealed record SyncFilter(bool? Read = null, bool? Saved = null, DateTime? Since = null, DateTime? Until = null);

public sealed record ArticleRef(long ArticleId, DateTime PublishedAt);

public sealed record ArticleRefPage(IReadOnlyList<ArticleRef> Items, ArticleCursor? Next);

/// <summary>An article as sync clients see it. <see cref="SubscriptionId"/> is null once its source is dropped.</summary>
public sealed record SyncItem(
    long ArticleId,
    long? SubscriptionId,
    string SourceTitle,
    string? SiteUrl,
    string Title,
    string? Url,
    string? Author,
    DateTime PublishedAt,
    DateTime FetchedAt,
    string ContentHtml,
    bool IsRead,
    bool IsSaved);

public sealed record FeedUnread(long SubscriptionId, int Count, DateTime Newest);

public sealed record ArticleDetail(
    long ArticleId,
    string SourceTitle,
    string Title,
    string? Url,
    string? Author,
    DateTime PublishedAt,
    string ContentHtml,
    bool IsRead,
    bool IsSaved,
    string? FullTextHtml = null,
    bool AlwaysFullText = false);

/// <summary>
/// Everything the reader shows and changes. Every query and update is filtered by the signed-in
/// user's id; ids from the browser are never trusted on their own. Each call uses its own
/// DbContext, because an interactive circuit outlives any single operation.
/// </summary>
public sealed class ReadingService(IDbContextFactory<SkimrDbContext> contexts, IArticleSearch search, TimeProvider time, SearchOptions? searchOptions = null)
{
    public const int PageSize = 40;

    public async Task<NavModel> GetNavAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var subscriptions = await db.Subscriptions
            .Where(s => s.UserId == userId)
            .Select(s => new
            {
                s.Id,
                s.FeedId,
                Title = s.CustomTitle ?? s.Feed!.Title ?? s.Feed.Url,
                HasError = s.Feed!.ConsecutiveFailures > 0,
                s.Feed.IconId,
                s.SortOrder,
                Folders = s.Folders.Select(f => new { f.FolderId, f.SortOrder }).ToList(),
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var folders = await db.Folders
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);

        // One grouped, index-only count per feed on (UserId, IsRead, FeedId) (PLAN D1). Feeds the
        // user no longer subscribes to are dropped here in memory: a per-row subscription check in
        // SQL made this 300 ms at 200k articles (results/phase5-perf).
        var subscribedFeeds = subscriptions.Select(s => s.FeedId).ToHashSet();
        var unread = (await db.UserArticles
                .Where(u => u.UserId == userId && !u.IsRead)
                .GroupBy(u => u.FeedId)
                .Select(g => new { FeedId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .Where(x => subscribedFeeds.Contains(x.FeedId))
            .ToDictionary(x => x.FeedId, x => x.Count);

        // Today: the last day's rows via (UserId, PublishedAt, ...), then the same in-memory filter.
        var since = time.GetUtcNow().UtcDateTime - ReaderView.TodayWindow;
        // No GROUP BY here: it made SQLite prefer the (UserId, FeedId) index and read every unread row.
        var todayUnread = (await db.UserArticles
                .Where(u => u.UserId == userId && u.PublishedAt >= since && !u.IsRead)
                .Select(u => u.FeedId)
                .ToListAsync(cancellationToken))
            .Count(subscribedFeeds.Contains);
        var saved = await db.UserArticles.CountAsync(u => u.UserId == userId && u.IsSaved, cancellationToken);
        var boards = await db.Boards
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new NavBoard(b.Id, b.Name, b.Articles.Count))
            .ToListAsync(cancellationToken);

        var navFolders = folders.Select(f =>
        {
            var sources = subscriptions
                .Where(s => s.Folders.Any(x => x.FolderId == f.Id))
                .OrderBy(s => s.Folders.First(x => x.FolderId == f.Id).SortOrder).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .Select(s => new NavSource(s.Id, s.FeedId, s.Title, unread.GetValueOrDefault(s.FeedId), s.HasError, s.IconId))
                .ToList();
            return new NavFolder(f.Id, f.Name, sources.Sum(s => s.Unread), sources);
        }).ToList();

        var unfiled = subscriptions
            .Where(s => s.Folders.Count == 0)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => new NavSource(s.Id, s.FeedId, s.Title, unread.GetValueOrDefault(s.FeedId), s.HasError, s.IconId))
            .ToList();

        return new NavModel(unread.Values.Sum(), todayUnread, saved, navFolders, unfiled, boards);
    }

    public async Task<ArticlePage> GetArticlesAsync(
        Guid userId, ReaderView view, ArticleSort sort, bool unreadOnly, ArticleCursor? after, CancellationToken cancellationToken, int pageSize = PageSize)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = await InViewAsync(db, userId, view, cancellationToken);
        if (unreadOnly && view.Kind is not (ViewKind.Saved or ViewKind.Board))
        {
            query = query.Where(u => !u.IsRead);
        }

        return await PageAsync(db, userId, query, sort, after, pageSize, cancellationToken);
    }

    /// <summary>
    /// The user's articles matching the text (title and plain-text body) and filters, newest
    /// first. Covers everything the user still holds, including Read Later from dropped sources.
    /// </summary>
    public async Task<ArticlePage> SearchAsync(Guid userId, SearchQuery query, ArticleCursor? after, CancellationToken cancellationToken, int pageSize = PageSize)
    {
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return new ArticlePage([], null);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var filtered = query.SourceId is not null || query.FolderId is not null || query.From is not null || query.To is not null || query.Read != ReadFilter.Any;
        var matching = search.MatchingArticleIds(db, query.Text.Trim(), filtered ? null : (searchOptions ?? SearchOptions.Default).CandidateLimit);
        var rows = db.UserArticles.Where(u => u.UserId == userId && matching.Contains(u.ArticleId));

        if (query.SourceId is { } sourceId)
        {
            rows = rows.Where(u => db.Subscriptions.Any(s => s.Id == sourceId && s.UserId == userId && s.FeedId == u.FeedId));
        }

        if (query.FolderId is { } folderId)
        {
            rows = rows.Where(u => db.SubscriptionFolders.Any(sf =>
                sf.FolderId == folderId && sf.Folder!.UserId == userId && sf.Subscription!.UserId == userId && sf.Subscription.FeedId == u.FeedId));
        }

        if (query.From is { } from)
        {
            rows = rows.Where(u => u.PublishedAt >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(u => u.PublishedAt < to);
        }

        rows = query.Read switch
        {
            ReadFilter.Unread => rows.Where(u => !u.IsRead),
            ReadFilter.Read => rows.Where(u => u.IsRead),
            _ => rows,
        };

        return await PageAsync(db, userId, rows, ArticleSort.Newest, after, pageSize, cancellationToken);
    }

    private static async Task<ArticlePage> PageAsync(
        SkimrDbContext db, Guid userId, IQueryable<UserArticle> query, ArticleSort sort, ArticleCursor? after, int pageSize, CancellationToken cancellationToken)
    {
        if (after is not null)
        {
            query = sort == ArticleSort.Newest
                ? query.Where(u => u.PublishedAt < after.PublishedAt || (u.PublishedAt == after.PublishedAt && u.ArticleId < after.ArticleId))
                : query.Where(u => u.PublishedAt > after.PublishedAt || (u.PublishedAt == after.PublishedAt && u.ArticleId > after.ArticleId));
        }

        query = sort == ArticleSort.Newest
            ? query.OrderByDescending(u => u.PublishedAt).ThenByDescending(u => u.ArticleId)
            : query.OrderBy(u => u.PublishedAt).ThenBy(u => u.ArticleId);

        var rows = await query
            .Take(pageSize + 1)
            .Select(u => new ArticleListItem(
                u.ArticleId,
                db.Subscriptions.Where(s => s.UserId == userId && s.FeedId == u.FeedId).Select(s => s.CustomTitle).FirstOrDefault()
                    ?? u.Article!.Feed!.Title ?? u.Article.Feed.Url,
                u.Article!.Title,
                u.Article.Snippet,
                u.Article.ThumbnailUrl,
                u.Article.Url,
                u.Article.Author,
                u.PublishedAt,
                u.IsRead,
                u.IsSaved))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var next = rows.Count > pageSize ? new ArticleCursor(rows[pageSize - 1].PublishedAt, rows[pageSize - 1].ArticleId) : null;
        return new ArticlePage(rows.Take(pageSize).ToList(), next);
    }

    /// <summary>Null unless the article is in one of this user's lists.</summary>
    public async Task<ArticleDetail?> GetArticleAsync(Guid userId, long articleId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.UserArticles
            .Where(u => u.UserId == userId && u.ArticleId == articleId)
            .Select(u => new ArticleDetail(
                u.ArticleId,
                db.Subscriptions.Where(s => s.UserId == userId && s.FeedId == u.FeedId).Select(s => s.CustomTitle).FirstOrDefault()
                    ?? u.Article!.Feed!.Title ?? u.Article.Feed.Url,
                u.Article!.Title,
                u.Article.Url,
                u.Article.Author,
                u.PublishedAt,
                u.Article.ContentHtml ?? "",
                u.IsRead,
                u.IsSaved,
                u.Article.FullTextHtml,
                db.Subscriptions.Any(s => s.UserId == userId && s.FeedId == u.FeedId && s.AlwaysFetchFullText)))
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<int> SetReadAsync(Guid userId, IReadOnlyCollection<long> articleIds, bool read, CancellationToken cancellationToken)
    {
        if (articleIds.Count == 0)
        {
            return 0;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        DateTime? readAt = read ? time.GetUtcNow().UtcDateTime : null;
        return await db.UserArticles
            .Where(u => u.UserId == userId && articleIds.Contains(u.ArticleId) && u.IsRead != read)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsRead, read).SetProperty(u => u.ReadAt, readAt), cancellationToken);
    }

    /// <returns>How many rows changed (already-saved rows don't count).</returns>
    public async Task<int> SetSavedAsync(Guid userId, IReadOnlyCollection<long> articleIds, bool saved, CancellationToken cancellationToken)
    {
        if (articleIds.Count == 0)
        {
            return 0;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        DateTime? savedAt = saved ? time.GetUtcNow().UtcDateTime : null;
        return await db.UserArticles
            .Where(u => u.UserId == userId && articleIds.Contains(u.ArticleId) && u.IsSaved != saved)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSaved, saved).SetProperty(u => u.SavedAt, savedAt), cancellationToken);
    }

    public async Task<bool> SetSavedAsync(Guid userId, long articleId, bool saved, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        DateTime? savedAt = saved ? time.GetUtcNow().UtcDateTime : null;
        return await db.UserArticles
            .Where(u => u.UserId == userId && u.ArticleId == articleId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSaved, saved).SetProperty(u => u.SavedAt, savedAt), cancellationToken) > 0;
    }

    /// <param name="olderThan">Only articles published longer ago than this; null for all.</param>
    public Task<int> MarkAllReadAsync(Guid userId, ReaderView view, TimeSpan? olderThan, CancellationToken cancellationToken) =>
        MarkReadBeforeAsync(userId, view, olderThan is { } age ? time.GetUtcNow().UtcDateTime - age : null, cancellationToken);

    /// <param name="publishedBefore">Only articles published before this; null for all. Sync clients send the time they last listed.</param>
    public async Task<int> MarkReadBeforeAsync(Guid userId, ReaderView view, DateTime? publishedBefore, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var query = (await InViewAsync(db, userId, view, cancellationToken)).Where(u => !u.IsRead);
        if (publishedBefore is { } cutoff)
        {
            query = query.Where(u => u.PublishedAt < cutoff);
        }

        return await query.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsRead, true).SetProperty(u => u.ReadAt, now), cancellationToken);
    }

    public async Task<ViewPreference> GetViewPreferenceAsync(Guid userId, ReaderView view, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var key = view.Key;
        return await db.ViewPreferences.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == userId && v.ViewKey == key, cancellationToken)
            ?? new ViewPreference { UserId = userId, ViewKey = key, UnreadOnly = view.Kind != ViewKind.Saved };
    }

    public async Task SaveViewPreferenceAsync(Guid userId, ReaderView view, ArticleLayout layout, ArticleSort sort, bool unreadOnly, CancellationToken cancellationToken)
    {
        try
        {
            await TrySaveViewPreferenceAsync(userId, view, layout, sort, unreadOnly, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two quick clicks both inserted the first row for this view; the second is now an update.
            await TrySaveViewPreferenceAsync(userId, view, layout, sort, unreadOnly, cancellationToken);
        }
    }

    private async Task TrySaveViewPreferenceAsync(Guid userId, ReaderView view, ArticleLayout layout, ArticleSort sort, bool unreadOnly, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var key = view.Key;
        var preference = await db.ViewPreferences.SingleOrDefaultAsync(v => v.UserId == userId && v.ViewKey == key, cancellationToken);
        if (preference is null)
        {
            preference = new ViewPreference { UserId = userId, ViewKey = key };
            db.ViewPreferences.Add(preference);
        }

        preference.Layout = layout;
        preference.Sort = sort;
        preference.UnreadOnly = unreadOnly;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MarkReadMode> GetMarkReadModeAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.UserPreferences.Where(p => p.UserId == userId).Select(p => (MarkReadMode?)p.MarkReadMode).SingleOrDefaultAsync(cancellationToken)
            ?? MarkReadMode.OnOpen;
    }

    public async Task SetMarkReadModeAsync(Guid userId, MarkReadMode mode, CancellationToken cancellationToken)
    {
        try
        {
            await TrySetMarkReadModeAsync(userId, mode, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race to insert the first row; it exists now, so this is an update.
            await TrySetMarkReadModeAsync(userId, mode, cancellationToken);
        }
    }

    private async Task TrySetMarkReadModeAsync(Guid userId, MarkReadMode mode, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var preferences = await db.UserPreferences.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (preferences is null)
        {
            db.UserPreferences.Add(new UserPreferences { UserId = userId, MarkReadMode = mode });
        }
        else
        {
            preferences.MarkReadMode = mode;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Sync APIs (Google Reader, Fever) ------------------------------------------------

    /// <summary>Article ids and dates in a view, keyset-paged like the reader's lists.</summary>
    public async Task<ArticleRefPage> GetArticleRefsAsync(
        Guid userId, ReaderView view, SyncFilter filter, ArticleSort sort, ArticleCursor? after, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = Filter(await InViewAsync(db, userId, view, cancellationToken), filter);
        if (after is not null)
        {
            query = sort == ArticleSort.Newest
                ? query.Where(u => u.PublishedAt < after.PublishedAt || (u.PublishedAt == after.PublishedAt && u.ArticleId < after.ArticleId))
                : query.Where(u => u.PublishedAt > after.PublishedAt || (u.PublishedAt == after.PublishedAt && u.ArticleId > after.ArticleId));
        }

        query = sort == ArticleSort.Newest
            ? query.OrderByDescending(u => u.PublishedAt).ThenByDescending(u => u.ArticleId)
            : query.OrderBy(u => u.PublishedAt).ThenBy(u => u.ArticleId);
        var rows = await query.Take(limit + 1).Select(u => new ArticleRef(u.ArticleId, u.PublishedAt)).ToListAsync(cancellationToken);
        var next = rows.Count > limit ? new ArticleCursor(rows[limit - 1].PublishedAt, rows[limit - 1].ArticleId) : null;
        return new ArticleRefPage(rows.Take(limit).ToList(), next);
    }

    /// <summary>
    /// Ids from subscribed sources, paged by id as Fever does: ascending after <paramref name="sinceId"/>,
    /// otherwise descending below <paramref name="maxId"/> (or from the newest).
    /// </summary>
    public async Task<IReadOnlyList<long>> GetArticleIdsByIdAsync(Guid userId, long? sinceId, long? maxId, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = await InViewAsync(db, userId, ReaderView.All, cancellationToken);
        if (sinceId is { } since)
        {
            return await query.Where(u => u.ArticleId > since).OrderBy(u => u.ArticleId).Take(limit).Select(u => u.ArticleId).ToListAsync(cancellationToken);
        }

        if (maxId is { } max)
        {
            query = query.Where(u => u.ArticleId < max);
        }

        return await query.OrderByDescending(u => u.ArticleId).Take(limit).Select(u => u.ArticleId).ToListAsync(cancellationToken);
    }

    /// <summary>Every unread id from subscribed sources, or every saved id (Read Later keeps dropped sources' articles).</summary>
    public async Task<IReadOnlyList<long>> GetAllArticleIdsAsync(Guid userId, SyncFilter filter, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = filter.Saved == true
            ? db.UserArticles.Where(u => u.UserId == userId)
            : await InViewAsync(db, userId, ReaderView.All, cancellationToken);
        return await Filter(rows, filter).Select(u => u.ArticleId).ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(Guid userId, ReaderView view, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await (await InViewAsync(db, userId, view, cancellationToken)).CountAsync(cancellationToken);
    }

    /// <summary>The user's articles among <paramref name="articleIds"/>, newest first; other ids are ignored.</summary>
    public async Task<IReadOnlyList<SyncItem>> GetSyncItemsAsync(Guid userId, IReadOnlyCollection<long> articleIds, CancellationToken cancellationToken)
    {
        if (articleIds.Count == 0)
        {
            return [];
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var subscriptions = await db.Subscriptions
            .Where(s => s.UserId == userId)
            .Select(s => new { s.Id, s.FeedId, s.CustomTitle })
            .ToDictionaryAsync(s => s.FeedId, cancellationToken);
        var rows = await db.UserArticles
            .Where(u => u.UserId == userId && articleIds.Contains(u.ArticleId))
            .OrderByDescending(u => u.PublishedAt).ThenByDescending(u => u.ArticleId)
            .Select(u => new
            {
                u.ArticleId,
                u.FeedId,
                FeedTitle = u.Article!.Feed!.Title ?? u.Article.Feed.Url,
                u.Article.Feed.SiteUrl,
                u.Article.Title,
                u.Article.Url,
                u.Article.Author,
                u.PublishedAt,
                u.Article.FetchedAt,
                u.Article.ContentHtml,
                u.IsRead,
                u.IsSaved,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Select(r =>
        {
            var subscription = subscriptions.GetValueOrDefault(r.FeedId);
            return new SyncItem(
                r.ArticleId, subscription?.Id, subscription?.CustomTitle ?? r.FeedTitle, r.SiteUrl, r.Title, r.Url, r.Author,
                r.PublishedAt, r.FetchedAt, r.ContentHtml ?? "", r.IsRead, r.IsSaved);
        }).ToList();
    }

    /// <summary>Unread count and newest unread date per subscribed source; the same grouped scan as the nav.</summary>
    public async Task<IReadOnlyList<FeedUnread>> GetUnreadByFeedAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var subscriptions = await db.Subscriptions.Where(s => s.UserId == userId).ToDictionaryAsync(s => s.FeedId, s => s.Id, cancellationToken);
        var unread = await db.UserArticles
            .Where(u => u.UserId == userId && !u.IsRead)
            .GroupBy(u => u.FeedId)
            .Select(g => new { FeedId = g.Key, Count = g.Count(), Newest = g.Max(u => u.PublishedAt) })
            .ToListAsync(cancellationToken);
        return unread
            .Where(x => subscriptions.ContainsKey(x.FeedId))
            .Select(x => new FeedUnread(subscriptions[x.FeedId], x.Count, x.Newest))
            .ToList();
    }

    private static IQueryable<UserArticle> Filter(IQueryable<UserArticle> query, SyncFilter filter)
    {
        if (filter.Read is { } read)
        {
            query = read ? query.Where(u => u.IsRead) : query.Where(u => !u.IsRead);
        }

        if (filter.Saved is { } saved)
        {
            query = saved ? query.Where(u => u.IsSaved) : query.Where(u => !u.IsSaved);
        }

        if (filter.Since is { } since)
        {
            query = query.Where(u => u.PublishedAt >= since);
        }

        if (filter.Until is { } until)
        {
            query = query.Where(u => u.PublishedAt < until);
        }

        return query;
    }

    /// <summary>
    /// The user's rows in a view. Source and folder views resolve the user's own feed ids first
    /// and filter on them directly, so the (UserId, FeedId, PublishedAt) index is used; a
    /// per-row subscription check took 450 ms for one source at 200k articles (results/phase5-perf).
    /// </summary>
    private async Task<IQueryable<UserArticle>> InViewAsync(SkimrDbContext db, Guid userId, ReaderView view, CancellationToken cancellationToken)
    {
        var mine = db.UserArticles.Where(u => u.UserId == userId);
        switch (view.Kind)
        {
            case ViewKind.Saved:
                // Read Later keeps articles even after their source is dropped (PLAN D41).
                return mine.Where(u => u.IsSaved);
            case ViewKind.Source:
                var feedId = await db.Subscriptions
                    .Where(s => s.Id == view.Id && s.UserId == userId)
                    .Select(s => (long?)s.FeedId)
                    .SingleOrDefaultAsync(cancellationToken);
                return feedId is { } id ? mine.Where(u => u.FeedId == id) : mine.Where(_ => false);
            case ViewKind.Folder:
                var feedIds = await db.SubscriptionFolders
                    .Where(sf => sf.FolderId == view.Id && sf.Folder!.UserId == userId && sf.Subscription!.UserId == userId)
                    .Select(sf => sf.Subscription!.FeedId)
                    .ToListAsync(cancellationToken);
                return mine.Where(u => feedIds.Contains(u.FeedId));
            case ViewKind.Board:
                // Boards keep articles even after their source is dropped, like Read Later.
                return mine.Where(u => db.BoardArticles.Any(b => b.BoardId == view.Id && b.Board!.UserId == userId && b.ArticleId == u.ArticleId));
            case ViewKind.Search:
                return mine.Where(_ => false);
        }

        var subscribed = mine.Where(u => db.Subscriptions.Any(s => s.UserId == userId && s.FeedId == u.FeedId));
        if (view.Kind == ViewKind.Today)
        {
            var since = time.GetUtcNow().UtcDateTime - ReaderView.TodayWindow;
            subscribed = subscribed.Where(u => u.PublishedAt >= since);
        }

        return subscribed;
    }
}
