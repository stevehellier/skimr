using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skimr.Domain;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;

namespace Skimr.Infrastructure.Ai;

public enum AiFailure
{
    /// <summary>The admin hasn't configured a provider.</summary>
    Off,

    /// <summary>The user hasn't turned AI on in Settings (PLAN D12).</summary>
    NotEnabled,

    NotFound,

    LimitReached,

    ProviderFailed,
}

public sealed record SummaryResult(string? Text, AiFailure? Failure = null, string? Message = null);

public sealed record AiFeedDetails(
    long Id, string Name, string Rule, IReadOnlyList<long> FolderIds, IReadOnlyList<long> SubscriptionIds, DateTime? LastCheckedAt, string? LastError);

/// <summary>
/// Summaries and AI feeds. Nothing is sent to the provider unless the admin configured one and
/// the user turned AI on (PLAN D12); every call counts toward the user's daily limit (D62).
/// </summary>
public sealed partial class AiService(
    IDbContextFactory<SkimrDbContext> contexts, AiOptions options, TimeProvider time, ILogger<AiService> logger, ISummariser? summariser = null)
{
    public const int MaxNameLength = 100;
    public const int MaxRuleLength = 1000;

    /// <summary>Batches per AI feed per worker pass, so one busy feed can't starve the rest.</summary>
    private const int BatchesPerPass = 5;

    public bool Available => options.Enabled && summariser is not null;

    public async Task<bool> IsEnabledForAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.UserPreferences.AnyAsync(p => p.UserId == userId && p.AiEnabled, cancellationToken);
    }

    public async Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            await TrySetEnabledAsync(userId, enabled, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race to insert the first preferences row; it exists now, so update it.
            await TrySetEnabledAsync(userId, enabled, cancellationToken);
        }
    }

    private async Task TrySetEnabledAsync(Guid userId, bool enabled, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var preferences = await db.UserPreferences.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (preferences is null)
        {
            db.UserPreferences.Add(new UserPreferences { UserId = userId, AiEnabled = enabled });
        }
        else
        {
            preferences.AiEnabled = enabled;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Summaries --------------------------------------------------------------------------

    public async Task<SummaryResult> SummariseAsync(Guid userId, long articleId, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return new(null, AiFailure.Off, "AI isn't set up on this server.");
        }

        if (!await IsEnabledForAsync(userId, cancellationToken))
        {
            return new(null, AiFailure.NotEnabled, "Turn on AI features in Settings first.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var article = await db.UserArticles
            .Where(u => u.UserId == userId && u.ArticleId == articleId)
            .Select(u => new { u.Article!.Title, Text = u.Article.SearchText ?? u.Article.Snippet ?? "" })
            .SingleOrDefaultAsync(cancellationToken);
        if (article is null)
        {
            return new(null, AiFailure.NotFound, "Article not found.");
        }

        var cached = await db.ArticleSummaries
            .Where(s => s.ArticleId == articleId && s.Model == options.Model)
            .Select(s => s.Text)
            .SingleOrDefaultAsync(cancellationToken);
        if (cached is not null)
        {
            return new(cached);
        }

        if (!await TryReserveCallAsync(userId, cancellationToken))
        {
            return new(null, AiFailure.LimitReached, $"You've used today's {options.DailyCallsPerUser} AI requests. The limit resets at midnight UTC.");
        }

        string text;
        try
        {
            text = await summariser!.SummariseAsync(new AiArticle(article.Title, article.Text), cancellationToken);
        }
        catch (AiException ex)
        {
            LogSummaryFailed(articleId, ex.Message);
            return new(null, AiFailure.ProviderFailed, ex.Message);
        }

        db.ArticleSummaries.Add(new ArticleSummary { ArticleId = articleId, Model = options.Model, Text = text, CreatedAt = time.GetUtcNow().UtcDateTime });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another user asked for the same article at the same moment; theirs is as good.
        }

        return new(text);
    }

    /// <summary>Counts one model call against today's limit; false if the limit is reached.</summary>
    internal async Task<bool> TryReserveCallAsync(Guid userId, CancellationToken cancellationToken)
    {
        var day = time.GetUtcNow().UtcDateTime.Date;
        var limit = options.DailyCallsPerUser;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            var updated = await db.AiUsage
                .Where(u => u.UserId == userId && u.Day == day && u.Calls < limit)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Calls, u => u.Calls + 1), cancellationToken);
            if (updated == 1)
            {
                return true;
            }

            if (await db.AiUsage.AnyAsync(u => u.UserId == userId && u.Day == day, cancellationToken))
            {
                return false;
            }

            db.AiUsage.Add(new AiUsage { UserId = userId, Day = day, Calls = 1 });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // Someone else created today's row first; go round and increment it.
            }
        }

        return false;
    }

    // ---- AI feeds ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<AiFeedDetails>> ListFeedsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.AiFeeds
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.Name)
            .Select(f => new AiFeedDetails(
                f.Id,
                f.Name,
                f.Rule,
                f.Sources.Where(s => s.FolderId != null).Select(s => s.FolderId!.Value).ToList(),
                f.Sources.Where(s => s.SubscriptionId != null).Select(s => s.SubscriptionId!.Value).ToList(),
                f.LastCheckedAt,
                f.LastError))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Starts with articles that arrive after now; older ones are never sent (cost, D62).</summary>
    public async Task<LibraryResult> CreateFeedAsync(
        Guid userId, string name, string rule, IReadOnlyCollection<long> folderIds, IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken)
    {
        if (Validate(name, rule) is { } error)
        {
            return LibraryResult.Fail(error);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var (folders, subscriptions) = await OwnedSourcesAsync(db, userId, folderIds, subscriptionIds, cancellationToken);
        if (folders.Count + subscriptions.Count == 0)
        {
            return LibraryResult.Fail("Choose at least one of your folders or sources.");
        }

        var feed = new AiFeed
        {
            UserId = userId,
            Name = name.Trim(),
            Rule = rule.Trim(),
            CreatedAt = time.GetUtcNow().UtcDateTime,
            LastCheckedArticleId = await db.Articles.MaxAsync(a => (long?)a.Id, cancellationToken) ?? 0,
        };
        feed.Sources.AddRange(folders.Select(id => new AiFeedSource { FolderId = id }));
        feed.Sources.AddRange(subscriptions.Select(id => new AiFeedSource { SubscriptionId = id }));
        db.AiFeeds.Add(feed);
        await db.SaveChangesAsync(cancellationToken);
        return LibraryResult.Ok(feed.Id);
    }

    /// <summary>Null arguments leave that part unchanged.</summary>
    public async Task<LibraryResult> UpdateFeedAsync(
        Guid userId, long feedId, string? name, string? rule, IReadOnlyCollection<long>? folderIds, IReadOnlyCollection<long>? subscriptionIds, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var feed = await db.AiFeeds.Include(f => f.Sources).SingleOrDefaultAsync(f => f.Id == feedId && f.UserId == userId, cancellationToken);
        if (feed is null)
        {
            return LibraryResult.Missing("AI feed not found.");
        }

        if (Validate(name ?? feed.Name, rule ?? feed.Rule) is { } error)
        {
            return LibraryResult.Fail(error);
        }

        feed.Name = (name ?? feed.Name).Trim();
        feed.Rule = (rule ?? feed.Rule).Trim();
        if (folderIds is not null || subscriptionIds is not null)
        {
            var (folders, subscriptions) = await OwnedSourcesAsync(
                db,
                userId,
                folderIds ?? feed.Sources.Where(s => s.FolderId != null).Select(s => s.FolderId!.Value).ToList(),
                subscriptionIds ?? feed.Sources.Where(s => s.SubscriptionId != null).Select(s => s.SubscriptionId!.Value).ToList(),
                cancellationToken);
            if (folders.Count + subscriptions.Count == 0)
            {
                return LibraryResult.Fail("Choose at least one of your folders or sources.");
            }

            feed.Sources.Clear();
            feed.Sources.AddRange(folders.Select(id => new AiFeedSource { FolderId = id }));
            feed.Sources.AddRange(subscriptions.Select(id => new AiFeedSource { SubscriptionId = id }));
        }

        await db.SaveChangesAsync(cancellationToken);
        return LibraryResult.Ok(feed.Id);
    }

    public async Task<LibraryResult> DeleteFeedAsync(Guid userId, long feedId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.AiFeeds.Where(f => f.Id == feedId && f.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0
            ? LibraryResult.Ok(feedId)
            : LibraryResult.Missing("AI feed not found.");
    }

    private static string? Validate(string name, string rule) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength ? $"Give the AI feed a name of up to {MaxNameLength} characters."
        : string.IsNullOrWhiteSpace(rule) || rule.Trim().Length > MaxRuleLength ? $"Describe what to match in up to {MaxRuleLength} characters."
        : null;

    /// <summary>Only the user's own folders and subscriptions; ids from the browser are never trusted.</summary>
    private static async Task<(List<long> Folders, List<long> Subscriptions)> OwnedSourcesAsync(
        SkimrDbContext db, Guid userId, IReadOnlyCollection<long> folderIds, IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken)
    {
        var folders = await db.Folders.Where(f => f.UserId == userId && folderIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(cancellationToken);
        var subscriptions = await db.Subscriptions.Where(s => s.UserId == userId && subscriptionIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(cancellationToken);
        return (folders, subscriptions);
    }

    // ---- Matching ---------------------------------------------------------------------------

    /// <summary>
    /// One pass over every AI feed whose owner has AI on: sends new articles from its sources to
    /// the model in batches and records the matches. Provider trouble leaves the batch for the
    /// next pass; an unusable answer skips it (so it never matches and never blocks the feed).
    /// </summary>
    public async Task MatchPendingAsync(CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return;
        }

        List<long> feedIds;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            feedIds = await db.AiFeeds
                .Where(f => db.UserPreferences.Any(p => p.UserId == f.UserId && p.AiEnabled))
                .OrderBy(f => f.Id)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var feedId in feedIds)
        {
            for (var batch = 0; batch < BatchesPerPass && await MatchBatchAsync(feedId, cancellationToken); batch++)
            {
            }
        }
    }

    /// <returns>True if a full batch was processed and there may be more.</returns>
    private async Task<bool> MatchBatchAsync(long feedId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var feed = await db.AiFeeds.Include(f => f.Sources).SingleOrDefaultAsync(f => f.Id == feedId, cancellationToken);
        if (feed is null)
        {
            return false;
        }

        var userId = feed.UserId;
        var subscriptionIds = feed.Sources.Where(s => s.SubscriptionId != null).Select(s => s.SubscriptionId!.Value).ToList();
        var folderIds = feed.Sources.Where(s => s.FolderId != null).Select(s => s.FolderId!.Value).ToList();
        var watched = await db.Subscriptions
            .Where(s => s.UserId == userId && (subscriptionIds.Contains(s.Id) || s.Folders.Any(f => folderIds.Contains(f.FolderId))))
            .Select(s => s.FeedId)
            .ToListAsync(cancellationToken);

        var cursor = feed.LastCheckedArticleId;
        var articles = await db.UserArticles
            .Where(u => u.UserId == userId && watched.Contains(u.FeedId) && u.ArticleId > cursor)
            .OrderBy(u => u.ArticleId)
            .Take(options.BatchSize)
            .Select(u => new { u.ArticleId, u.Article!.Title, Text = u.Article.SearchText ?? u.Article.Snippet ?? "" })
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        if (articles.Count == 0)
        {
            return false;
        }

        if (!await TryReserveCallAsync(userId, cancellationToken))
        {
            feed.LastError = "Today's AI limit is used up; matching resumes after midnight UTC.";
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        try
        {
            var matches = await summariser!.MatchAsync(feed.Rule, articles.Select(a => new AiArticle(a.Title, a.Text)).ToList(), cancellationToken);
            db.AiFeedMatches.AddRange(matches.Select(i => new AiFeedMatch { AiFeedId = feed.Id, ArticleId = articles[i].ArticleId, MatchedAt = now }));
            feed.LastError = null;
        }
        catch (AiException ex) when (ex.Transient)
        {
            feed.LastError = ex.Message;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        catch (AiException ex)
        {
            // Unusable answer: skip these articles rather than retry them forever.
            LogMatchFailed(feed.Id, ex.Message);
            feed.LastError = ex.Message;
        }

        feed.LastCheckedArticleId = articles[^1].ArticleId;
        feed.LastCheckedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return articles.Count == options.BatchSize;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI summary for article {ArticleId} failed: {Reason}")]
    private partial void LogSummaryFailed(long articleId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI feed {AiFeedId} skipped a batch: {Reason}")]
    private partial void LogMatchFailed(long aiFeedId, string reason);
}

/// <summary>Runs the AI-feed matcher every <see cref="AiOptions.MatchInterval"/> while AI is configured.</summary>
internal sealed partial class AiFeedWorker(IServiceScopeFactory scopes, AiOptions options, TimeProvider time, ILogger<AiFeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(options.MatchInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AiService>().MatchPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "AI feed matching failed")]
    private partial void LogFailed(Exception ex);
}
