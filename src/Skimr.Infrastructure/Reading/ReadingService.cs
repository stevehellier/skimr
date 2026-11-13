using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record NavSource(long SubscriptionId, long FeedId, string Title, int Unread, bool HasError);

public sealed record NavFolder(long Id, string Name, int Unread, IReadOnlyList<NavSource> Sources);

public sealed record NavModel(int AllUnread, int TodayUnread, int SavedCount, IReadOnlyList<NavFolder> Folders, IReadOnlyList<NavSource> Unfiled)
{
    public static readonly NavModel Empty = new(0, 0, 0, [], []);
}

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

public sealed record ArticleDetail(
    long ArticleId,
    string SourceTitle,
    string Title,
    string? Url,
    string? Author,
    DateTime PublishedAt,
    string ContentHtml,
    bool IsRead,
    bool IsSaved);

/// <summary>
/// Everything the reader shows and changes. Every query and update is filtered by the signed-in
/// user's id; ids from the browser are never trusted on their own. Each call uses its own
/// DbContext, because an interactive circuit outlives any single operation.
/// </summary>
public sealed class ReadingService(IDbContextFactory<SkimrDbContext> contexts, TimeProvider time)
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

        // One grouped, index-only count per source (PLAN D1); folders and totals are sums of these.
        var unread = await SubscribedUnread(db, userId)
            .GroupBy(u => u.FeedId)
            .Select(g => new { FeedId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FeedId, x => x.Count, cancellationToken);

        var since = time.GetUtcNow().UtcDateTime - ReaderView.TodayWindow;
        var todayUnread = await SubscribedUnread(db, userId).CountAsync(u => u.PublishedAt >= since, cancellationToken);
        var saved = await db.UserArticles.CountAsync(u => u.UserId == userId && u.IsSaved, cancellationToken);

        var navFolders = folders.Select(f =>
        {
            var sources = subscriptions
                .Where(s => s.Folders.Any(x => x.FolderId == f.Id))
                .OrderBy(s => s.Folders.First(x => x.FolderId == f.Id).SortOrder).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .Select(s => new NavSource(s.Id, s.FeedId, s.Title, unread.GetValueOrDefault(s.FeedId), s.HasError))
                .ToList();
            return new NavFolder(f.Id, f.Name, sources.Sum(s => s.Unread), sources);
        }).ToList();

        var unfiled = subscriptions
            .Where(s => s.Folders.Count == 0)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => new NavSource(s.Id, s.FeedId, s.Title, unread.GetValueOrDefault(s.FeedId), s.HasError))
            .ToList();

        return new NavModel(unread.Values.Sum(), todayUnread, saved, navFolders, unfiled);
    }

    public async Task<ArticlePage> GetArticlesAsync(
        Guid userId, ReaderView view, ArticleSort sort, bool unreadOnly, ArticleCursor? after, CancellationToken cancellationToken, int pageSize = PageSize)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = InView(db, userId, view);
        if (unreadOnly && view.Kind != ViewKind.Saved)
        {
            query = query.Where(u => !u.IsRead);
        }

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
                u.IsSaved))
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

    public async Task<bool> SetSavedAsync(Guid userId, long articleId, bool saved, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        DateTime? savedAt = saved ? time.GetUtcNow().UtcDateTime : null;
        return await db.UserArticles
            .Where(u => u.UserId == userId && u.ArticleId == articleId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSaved, saved).SetProperty(u => u.SavedAt, savedAt), cancellationToken) > 0;
    }

    /// <param name="olderThan">Only articles published longer ago than this; null for all.</param>
    public async Task<int> MarkAllReadAsync(Guid userId, ReaderView view, TimeSpan? olderThan, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var query = InView(db, userId, view).Where(u => !u.IsRead);
        if (olderThan is { } age)
        {
            var cutoff = now - age;
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

    /// <summary>Unread articles from sources the user still subscribes to. Saved articles from dropped sources don't count.</summary>
    private static IQueryable<UserArticle> SubscribedUnread(SkimrDbContext db, Guid userId) =>
        db.UserArticles.Where(u => u.UserId == userId && !u.IsRead
            && db.Subscriptions.Any(s => s.UserId == userId && s.FeedId == u.FeedId));

    private IQueryable<UserArticle> InView(SkimrDbContext db, Guid userId, ReaderView view)
    {
        var mine = db.UserArticles.Where(u => u.UserId == userId);
        switch (view.Kind)
        {
            case ViewKind.Saved:
                // Read Later keeps articles even after their source is dropped (PLAN D41).
                return mine.Where(u => u.IsSaved);
            case ViewKind.Source:
                return mine.Where(u => db.Subscriptions.Any(s => s.Id == view.Id && s.UserId == userId && s.FeedId == u.FeedId));
            case ViewKind.Folder:
                return mine.Where(u => db.SubscriptionFolders.Any(sf =>
                    sf.FolderId == view.Id && sf.Folder!.UserId == userId && sf.Subscription!.UserId == userId && sf.Subscription.FeedId == u.FeedId));
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
