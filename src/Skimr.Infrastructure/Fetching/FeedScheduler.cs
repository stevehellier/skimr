using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Fetching;

/// <summary>
/// Every tick, queues feeds that are due and have at least one subscriber. A fixed pool of
/// workers drains the queue, so at most <see cref="FetchOptions.Concurrency"/> fetches run at once.
/// A feed is never queued twice while it's waiting or being fetched.
/// </summary>
internal sealed partial class FeedScheduler(
    IServiceScopeFactory scopes,
    FetchOptions options,
    TimeProvider time,
    ILogger<FeedScheduler> logger) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private const int MaxDuePerTick = 500;

    private readonly Channel<long> _queue = Channel.CreateBounded<long>(Math.Max(options.Concurrency * 4, 16));
    private readonly ConcurrentDictionary<long, byte> _queued = new();

    /// <summary>Queues a feed now, e.g. a new subscription or a manual refresh.</summary>
    public async ValueTask EnqueueAsync(long feedId, CancellationToken cancellationToken)
    {
        if (_queued.TryAdd(feedId, 0))
        {
            await _queue.Writer.WriteAsync(feedId, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            LogDisabled();
            return;
        }

        var workers = StartWorkers(stoppingToken);
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            do
            {
                try
                {
                    await EnqueueDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }

        _queue.Writer.TryComplete();
        await workers;
    }

    internal async Task<int> EnqueueDueAsync(CancellationToken cancellationToken)
    {
        List<long> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
            var now = time.GetUtcNow().UtcDateTime;
            due = await db.Feeds
                .Where(f => f.NextFetchAt <= now && f.Subscriptions.Any())
                .OrderBy(f => f.NextFetchAt)
                .Select(f => f.Id)
                .Take(MaxDuePerTick)
                .ToListAsync(cancellationToken);
        }

        var queued = 0;
        foreach (var id in due.Where(id => !_queued.ContainsKey(id)))
        {
            await EnqueueAsync(id, cancellationToken);
            queued++;
        }

        return queued;
    }

    internal Task StartWorkers(CancellationToken cancellationToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(_ => Task.Run(() => WorkAsync(cancellationToken), CancellationToken.None)));

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var feedId in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IFeedRefresher>().RefreshAsync(feedId, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Unexpected (e.g. database) error: record it on the feed so it shows in source
                    // health, and push the feed back so it isn't retried every tick.
                    LogRefreshFailed(ex, feedId);
                    await PostponeAsync(feedId, ex, cancellationToken);
                }
                finally
                {
                    _queued.TryRemove(feedId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task PostponeAsync(long feedId, Exception error, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
            var now = time.GetUtcNow().UtcDateTime;
            var next = now.AddMinutes(options.DefaultIntervalMinutes);
            var message = StoredText.Fit("Internal error: " + error.Message.Split('\n')[0].Trim(), 1000);
            await db.Feeds.Where(f => f.Id == feedId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(f => f.NextFetchAt, next)
                        .SetProperty(f => f.LastErrorAt, now)
                        .SetProperty(f => f.LastError, message)
                        .SetProperty(f => f.ConsecutiveFailures, f => f.ConsecutiveFailures + 1),
                    cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogRefreshFailed(ex, feedId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed fetching is disabled (Fetch:Enabled = false)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler tick failed")]
    private partial void LogTickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing feed {FeedId} failed unexpectedly")]
    private partial void LogRefreshFailed(Exception ex, long feedId);
}
