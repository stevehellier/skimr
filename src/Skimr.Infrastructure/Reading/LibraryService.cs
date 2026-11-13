using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record LibraryResult(bool Succeeded, string? Error = null, long? Id = null)
{
    public static LibraryResult Ok(long? id = null) => new(true, null, id);

    public static LibraryResult Fail(string error) => new(false, error);
}

public sealed record DiscoveredSource(string Url, string? Title);

public sealed record SourceDetails(
    long SubscriptionId,
    string FeedUrl,
    string? SiteUrl,
    string Title,
    string? CustomTitle,
    int? RefreshMinutes,
    IReadOnlyList<long> FolderIds,
    DateTime? LastSuccessAt,
    DateTime? LastErrorAt,
    string? LastError,
    int ConsecutiveFailures,
    DateTime NextFetchAt);

public sealed record FolderDetails(long Id, string Name, int SortOrder);

public enum MoveDirection
{
    Up,
    Down,
}

/// <summary>
/// Sources and folders: subscribe, unsubscribe, rename, reorder, file into folders. Every
/// operation checks that the subscription or folder belongs to the signed-in user.
/// </summary>
internal sealed class LibraryService(IDbContextFactory<SkimrDbContext> contexts, FeedDiscovery discovery, FeedScheduler scheduler, TimeProvider time)
{
    /// <summary>A new subscriber to an existing feed gets its most recent articles (PLAN D1).</summary>
    public const int BackfillArticles = 200;

    public async Task<(IReadOnlyList<DiscoveredSource> Found, string? Error)> DiscoverAsync(string input, CancellationToken cancellationToken)
    {
        try
        {
            var found = await discovery.DiscoverAsync(input, cancellationToken);
            return (found.Select(f => new DiscoveredSource(f.Url, f.Title)).ToList(), found.Count == 0 ? "No feeds found at that address." : null);
        }
        catch (FetchException ex)
        {
            return ([], ex.Message);
        }
    }

    /// <param name="titleHint">A title to show until the first fetch fills in the real one (e.g. from OPML).</param>
    public async Task<LibraryResult> SubscribeAsync(Guid userId, string feedUrl, IReadOnlyCollection<long> folderIds, CancellationToken cancellationToken, string? titleHint = null)
    {
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsoluteUri.Length > 2048)
        {
            return LibraryResult.Fail("That isn't a valid http(s) feed address.");
        }

        var url = uri.AbsoluteUri;
        var now = time.GetUtcNow().UtcDateTime;
        var feedId = await FindOrCreateFeedAsync(url, StoredText.Fit(titleHint?.Trim(), 500), now, cancellationToken);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var existing = await db.Subscriptions.Where(s => s.UserId == userId && s.FeedId == feedId).Select(s => (long?)s.Id).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return LibraryResult.Ok(existing);
        }

        var nextOrder = (await db.Subscriptions.Where(s => s.UserId == userId).MaxAsync(s => (int?)s.SortOrder, cancellationToken) ?? -1) + 1;
        var subscription = new Subscription { UserId = userId, FeedId = feedId, CreatedAt = now, SortOrder = nextOrder };
        db.Subscriptions.Add(subscription);
        await AddToFoldersAsync(db, userId, subscription, folderIds, cancellationToken);

        // Back-fill recent articles the feed already has; skip any the user still holds (e.g. saved from before).
        var recent = await db.Articles
            .Where(a => a.FeedId == feedId && !db.UserArticles.Any(u => u.UserId == userId && u.ArticleId == a.Id))
            .OrderByDescending(a => a.PublishedAt)
            .Take(BackfillArticles)
            .Select(a => new { a.Id, a.PublishedAt })
            .ToListAsync(cancellationToken);
        db.UserArticles.AddRange(recent.Select(a => new UserArticle { UserId = userId, ArticleId = a.Id, FeedId = feedId, PublishedAt = a.PublishedAt }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A double-click or a second tab subscribed the same user at the same moment and won.
            await using var again = await contexts.CreateDbContextAsync(cancellationToken);
            var winner = await again.Subscriptions.Where(s => s.UserId == userId && s.FeedId == feedId).Select(s => (long?)s.Id).SingleOrDefaultAsync(cancellationToken);
            return winner is { } id ? LibraryResult.Ok(id) : LibraryResult.Fail("Couldn't subscribe just now. Try again.");
        }

        scheduler.TryEnqueue(feedId);
        return LibraryResult.Ok(subscription.Id);
    }

    /// <summary>Drops the subscription and its history. Saved and boarded articles stay (PLAN D41).</summary>
    public async Task<LibraryResult> UnsubscribeAsync(Guid userId, long subscriptionId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == subscriptionId && s.UserId == userId, cancellationToken);
        if (subscription is null)
        {
            return LibraryResult.Fail("Source not found.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.UserArticles
            .Where(u => u.UserId == userId && u.FeedId == subscription.FeedId && !u.IsSaved
                && !db.BoardArticles.Any(b => b.ArticleId == u.ArticleId && b.Board!.UserId == userId))
            .ExecuteDeleteAsync(cancellationToken);
        db.Subscriptions.Remove(subscription);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return LibraryResult.Ok(subscriptionId);
    }

    public async Task<LibraryResult> UpdateSourceAsync(
        Guid userId, long subscriptionId, string? customTitle, int? refreshMinutes, IReadOnlyCollection<long> folderIds, CancellationToken cancellationToken)
    {
        if (refreshMinutes is { } minutes && (minutes < FetchOptions.MinIntervalMinutes || minutes > 24 * 60))
        {
            return LibraryResult.Fail($"Refresh interval must be between {FetchOptions.MinIntervalMinutes} minutes and 24 hours.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var subscription = await db.Subscriptions.Include(s => s.Folders).SingleOrDefaultAsync(s => s.Id == subscriptionId && s.UserId == userId, cancellationToken);
        if (subscription is null)
        {
            return LibraryResult.Fail("Source not found.");
        }

        subscription.CustomTitle = StoredText.Fit(string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim(), 500);
        subscription.RefreshMinutes = refreshMinutes;

        var wanted = folderIds.ToHashSet();
        subscription.Folders.RemoveAll(f => !wanted.Contains(f.FolderId));
        await AddToFoldersAsync(db, userId, subscription, wanted.Except(subscription.Folders.Select(f => f.FolderId)).ToList(), cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return LibraryResult.Ok(subscriptionId);
    }

    /// <summary>Files an existing subscription into one more folder (both must be the user's).</summary>
    public async Task<LibraryResult> AddToFolderAsync(Guid userId, long subscriptionId, long folderId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var subscription = await db.Subscriptions.Include(s => s.Folders).SingleOrDefaultAsync(s => s.Id == subscriptionId && s.UserId == userId, cancellationToken);
        if (subscription is null)
        {
            return LibraryResult.Fail("Source not found.");
        }

        if (subscription.Folders.All(f => f.FolderId != folderId))
        {
            await AddToFoldersAsync(db, userId, subscription, [folderId], cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return LibraryResult.Ok(subscriptionId);
    }

    /// <summary>Asks for an immediate fetch.</summary>
    public async Task<LibraryResult> RefreshNowAsync(Guid userId, long subscriptionId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var feedId = await db.Subscriptions.Where(s => s.Id == subscriptionId && s.UserId == userId).Select(s => (long?)s.FeedId).SingleOrDefaultAsync(cancellationToken);
        if (feedId is null)
        {
            return LibraryResult.Fail("Source not found.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await db.Feeds.Where(f => f.Id == feedId).ExecuteUpdateAsync(s => s.SetProperty(f => f.NextFetchAt, now), cancellationToken);
        scheduler.TryEnqueue(feedId.Value);
        return LibraryResult.Ok(subscriptionId);
    }

    public async Task<IReadOnlyList<SourceDetails>> ListSourcesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.SortOrder)
            .Select(s => new SourceDetails(
                s.Id,
                s.Feed!.Url,
                s.Feed.SiteUrl,
                s.CustomTitle ?? s.Feed.Title ?? s.Feed.Url,
                s.CustomTitle,
                s.RefreshMinutes,
                s.Folders.Select(f => f.FolderId).ToList(),
                s.Feed.LastSuccessAt,
                s.Feed.LastErrorAt,
                s.Feed.LastError,
                s.Feed.ConsecutiveFailures,
                s.Feed.NextFetchAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows;
    }

    /// <summary>Moves a source within a folder, or among unfiled sources when <paramref name="folderId"/> is null.</summary>
    public async Task<LibraryResult> MoveSourceAsync(Guid userId, long? folderId, long subscriptionId, MoveDirection direction, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (folderId is { } id)
        {
            var links = await db.SubscriptionFolders
                .Where(sf => sf.FolderId == id && sf.Folder!.UserId == userId)
                .OrderBy(sf => sf.SortOrder).ThenBy(sf => sf.SubscriptionId)
                .ToListAsync(cancellationToken);
            if (!Swap(links, l => l.SubscriptionId == subscriptionId, direction, (l, order) => l.SortOrder = order))
            {
                return LibraryResult.Fail("Source not found.");
            }
        }
        else
        {
            var subscriptions = await db.Subscriptions
                .Where(s => s.UserId == userId && !s.Folders.Any())
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .ToListAsync(cancellationToken);
            if (!Swap(subscriptions, s => s.Id == subscriptionId, direction, (s, order) => s.SortOrder = order))
            {
                return LibraryResult.Fail("Source not found.");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return LibraryResult.Ok(subscriptionId);
    }

    // ---- Folders -----------------------------------------------------------------------

    public async Task<IReadOnlyList<FolderDetails>> ListFoldersAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Folders.Where(f => f.UserId == userId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new FolderDetails(f.Id, f.Name, f.SortOrder)).ToListAsync(cancellationToken);
    }

    public async Task<LibraryResult> CreateFolderAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        var clean = StoredText.Fit(name?.Trim(), 100);
        if (string.IsNullOrEmpty(clean))
        {
            return LibraryResult.Fail("Give the folder a name.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.Folders.AnyAsync(f => f.UserId == userId && f.Name == clean, cancellationToken))
        {
            return LibraryResult.Fail("You already have a folder with that name.");
        }

        var order = (await db.Folders.Where(f => f.UserId == userId).MaxAsync(f => (int?)f.SortOrder, cancellationToken) ?? -1) + 1;
        var folder = new Folder { UserId = userId, Name = clean, SortOrder = order };
        db.Folders.Add(folder);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Same name created at the same moment (double-click): the unique index caught it.
            return LibraryResult.Fail("You already have a folder with that name.");
        }

        return LibraryResult.Ok(folder.Id);
    }

    public async Task<LibraryResult> RenameFolderAsync(Guid userId, long folderId, string name, CancellationToken cancellationToken)
    {
        var clean = StoredText.Fit(name?.Trim(), 100);
        if (string.IsNullOrEmpty(clean))
        {
            return LibraryResult.Fail("Give the folder a name.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folder = await db.Folders.SingleOrDefaultAsync(f => f.Id == folderId && f.UserId == userId, cancellationToken);
        if (folder is null)
        {
            return LibraryResult.Fail("Folder not found.");
        }

        if (await db.Folders.AnyAsync(f => f.UserId == userId && f.Name == clean && f.Id != folderId, cancellationToken))
        {
            return LibraryResult.Fail("You already have a folder with that name.");
        }

        folder.Name = clean;
        await db.SaveChangesAsync(cancellationToken);
        return LibraryResult.Ok(folderId);
    }

    /// <summary>Deletes the folder only; its sources stay subscribed (unfiled if it was their only folder).</summary>
    public async Task<LibraryResult> DeleteFolderAsync(Guid userId, long folderId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Folders.Where(f => f.Id == folderId && f.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0 ? LibraryResult.Ok(folderId) : LibraryResult.Fail("Folder not found.");
    }

    public async Task<LibraryResult> MoveFolderAsync(Guid userId, long folderId, MoveDirection direction, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var folders = await db.Folders.Where(f => f.UserId == userId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(cancellationToken);
        if (!Swap(folders, f => f.Id == folderId, direction, (f, order) => f.SortOrder = order))
        {
            return LibraryResult.Fail("Folder not found.");
        }

        await db.SaveChangesAsync(cancellationToken);
        return LibraryResult.Ok(folderId);
    }

    // ---- Helpers -----------------------------------------------------------------------

    /// <summary>
    /// Shared feed row for a URL. Two users adding a brand-new URL at once both try to insert;
    /// the unique index stops the second, which then uses the first one's row.
    /// </summary>
    private async Task<long> FindOrCreateFeedAsync(string url, string? title, DateTime now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var existing = await db.Feeds.Where(f => f.Url == url).Select(f => (long?)f.Id).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var feed = new Feed { Url = url, Title = string.IsNullOrEmpty(title) ? null : title, CreatedAt = now, NextFetchAt = now };
        db.Feeds.Add(feed);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return feed.Id;
        }
        catch (DbUpdateException)
        {
            await using var again = await contexts.CreateDbContextAsync(cancellationToken);
            return await again.Feeds.Where(f => f.Url == url).Select(f => f.Id).SingleAsync(cancellationToken);
        }
    }

    private static async Task AddToFoldersAsync(SkimrDbContext db, Guid userId, Subscription subscription, IReadOnlyCollection<long> folderIds, CancellationToken cancellationToken)
    {
        if (folderIds.Count == 0)
        {
            return;
        }

        // Only the user's own folders: ids from the browser are never trusted.
        var owned = await db.Folders.Where(f => f.UserId == userId && folderIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(cancellationToken);
        foreach (var folderId in owned)
        {
            var order = (await db.SubscriptionFolders.Where(sf => sf.FolderId == folderId).MaxAsync(sf => (int?)sf.SortOrder, cancellationToken) ?? -1) + 1;
            subscription.Folders.Add(new SubscriptionFolder { FolderId = folderId, SortOrder = order });
        }
    }

    /// <summary>Renumbers the list 0..n with the target moved one place. False if the target isn't in the list.</summary>
    private static bool Swap<T>(List<T> items, Func<T, bool> isTarget, MoveDirection direction, Action<T, int> setOrder)
    {
        var index = items.FindIndex(i => isTarget(i));
        if (index < 0)
        {
            return false;
        }

        var other = direction == MoveDirection.Up ? index - 1 : index + 1;
        if (other >= 0 && other < items.Count)
        {
            (items[index], items[other]) = (items[other], items[index]);
        }

        for (var i = 0; i < items.Count; i++)
        {
            setOrder(items[i], i);
        }

        return true;
    }
}
