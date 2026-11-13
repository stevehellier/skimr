using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record RetentionResult(int UserRows, int Articles, int Feeds);

/// <summary>
/// Purges old read articles (PLAN D3, D52):
/// 1. a user's row goes once it's read, not saved, not on any of their boards, and published
///    more than <see cref="RetentionOptions.Days"/> ago;
/// 2. an article goes once nobody holds it, nobody boarded it, and it hasn't been in its feed
///    for that long (so a purged item still in the feed doesn't come back as unread);
/// 3. a feed goes once nobody subscribes and it has no articles left.
/// Deletes run in small batches so SQLite's single writer is never held for long.
/// </summary>
public sealed partial class RetentionService(IDbContextFactory<SkimrDbContext> contexts, RetentionOptions options, TimeProvider time, ILogger<RetentionService> logger)
{
    private const int BatchSize = 1000;

    public async Task<RetentionResult> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-options.Days);

        var rows = await PurgeUserRowsAsync(cutoff, cancellationToken);

        var articles = await InBatchesAsync(db => db.Articles
            .Where(a => a.LastSeenAt < cutoff
                && !db.UserArticles.Any(u => u.ArticleId == a.Id)
                && !db.BoardArticles.Any(b => b.ArticleId == a.Id))
            .Take(BatchSize), cancellationToken);

        int feeds;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            feeds = await db.Feeds
                .Where(f => f.CreatedAt < cutoff && !f.Subscriptions.Any() && !db.Articles.Any(a => a.FeedId == f.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        LogPurged(rows, articles, feeds);
        return new RetentionResult(rows, articles, feeds);
    }

    /// <summary>
    /// UserArticles has a composite key, so a Take() inside ExecuteDelete becomes a correlated
    /// EXISTS that scans the whole table on every batch. Instead read a batch of keys, then
    /// delete each user's share by primary key.
    /// </summary>
    private async Task<int> PurgeUserRowsAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        List<Guid> users;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            users = await db.Users.Select(u => u.Id).ToListAsync(cancellationToken);
        }

        // User by user, so each batch is a range scan on (UserId, PublishedAt, ...) rather than a
        // scan of every row in the table (results/phase5-perf).
        var total = 0;
        foreach (var userId in users)
        {
            while (true)
            {
                await using var db = await contexts.CreateDbContextAsync(cancellationToken);
                var articleIds = await db.UserArticles
                    .Where(u => u.UserId == userId && u.PublishedAt < cutoff && u.IsRead && !u.IsSaved
                        && !db.BoardArticles.Any(b => b.ArticleId == u.ArticleId && b.Board!.UserId == userId))
                    .Select(u => u.ArticleId)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
                if (articleIds.Count == 0)
                {
                    break;
                }

                total += await db.UserArticles
                    .Where(u => u.UserId == userId && articleIds.Contains(u.ArticleId))
                    .ExecuteDeleteAsync(cancellationToken);
                if (articleIds.Count < BatchSize)
                {
                    break;
                }
            }
        }

        return total;
    }

    private async Task<int> InBatchesAsync<T>(Func<SkimrDbContext, IQueryable<T>> batch, CancellationToken cancellationToken)
        where T : class
    {
        var total = 0;
        int deleted;
        do
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            deleted = await batch(db).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention removed {Rows} read article rows, {Articles} articles and {Feeds} unused feeds")]
    private partial void LogPurged(int rows, int articles, int feeds);
}

/// <summary>Runs the purge a few minutes after start, then every six hours.</summary>
internal sealed partial class RetentionWorker(IServiceScopeFactory scopes, RetentionOptions options, TimeProvider time, ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention run failed")]
    private partial void LogFailed(Exception ex);
}
