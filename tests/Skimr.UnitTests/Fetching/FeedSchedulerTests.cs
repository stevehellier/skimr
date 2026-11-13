using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.UnitTests.Fetching;

public sealed class FeedSchedulerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new(new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
    private readonly BlockingRefresher _refresher = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    private FeedScheduler Scheduler(int concurrency)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => _db.Create())
            .AddSingleton<IFeedRefresher>(_refresher)
            .BuildServiceProvider();
        return new FeedScheduler(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FetchOptions { Concurrency = concurrency },
            _time,
            NullLogger<FeedScheduler>.Instance);
    }

    private long AddFeed(TimeSpan dueIn, bool subscribed = true)
    {
        using var db = _db.Create();
        var feed = new Feed { Url = $"https://f{Guid.NewGuid():N}.example.com/", CreatedAt = _time.UtcNow, NextFetchAt = _time.UtcNow + dueIn };
        db.Feeds.Add(feed);
        db.SaveChanges();
        if (subscribed)
        {
            db.Subscriptions.Add(new Subscription { UserId = Guid.NewGuid(), FeedId = feed.Id, CreatedAt = _time.UtcNow });
            db.SaveChanges();
        }

        return feed.Id;
    }

    [Fact]
    public async Task Only_due_feeds_with_subscribers_are_queued()
    {
        var due = AddFeed(TimeSpan.FromMinutes(-1));
        AddFeed(TimeSpan.FromMinutes(10));
        AddFeed(TimeSpan.FromMinutes(-1), subscribed: false);
        var scheduler = Scheduler(2);

        (await scheduler.EnqueueDueAsync(Ct)).Should().Be(1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var workers = scheduler.StartWorkers(cts.Token);
        _refresher.Release(1);
        await _refresher.WaitForCompletedAsync(1, Ct);
        _refresher.Completed.Should().Equal(due);
        await cts.CancelAsync();
        await workers;
    }

    [Fact]
    public async Task A_feed_already_queued_is_not_queued_again()
    {
        AddFeed(TimeSpan.FromMinutes(-1));
        var scheduler = Scheduler(2);

        (await scheduler.EnqueueDueAsync(Ct)).Should().Be(1);
        (await scheduler.EnqueueDueAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        for (var i = 0; i < 10; i++)
        {
            AddFeed(TimeSpan.FromMinutes(-1));
        }

        var scheduler = Scheduler(3);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var workers = scheduler.StartWorkers(cts.Token);
        await scheduler.EnqueueDueAsync(Ct);

        await _refresher.WaitForRunningAsync(3, Ct);
        await Task.Delay(200, Ct);
        _refresher.MaxRunning.Should().Be(3);

        _refresher.Release(10);
        await _refresher.WaitForCompletedAsync(10, Ct);
        _refresher.MaxRunning.Should().Be(3);
        await cts.CancelAsync();
        await workers;
    }

    [Fact]
    public async Task Unexpected_errors_postpone_the_feed_instead_of_retrying_every_tick()
    {
        var id = AddFeed(TimeSpan.FromMinutes(-1));
        _refresher.Throw = true;
        var scheduler = Scheduler(1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var workers = scheduler.StartWorkers(cts.Token);

        await scheduler.EnqueueDueAsync(Ct);
        _refresher.Release(1);
        await _refresher.WaitForCompletedAsync(1, Ct);
        await Task.Delay(200, Ct);

        using var db = _db.Create();
        db.Feeds.Single(f => f.Id == id).NextFetchAt.Should().Be(_time.UtcNow.AddMinutes(30));
        await cts.CancelAsync();
        await workers;
    }

    private sealed class BlockingRefresher : IFeedRefresher
    {
        private readonly SemaphoreSlim _gate = new(0);
        private int _running;
        private int _maxRunning;

        public ConcurrentQueue<long> CompletedQueue { get; } = new();

        public IReadOnlyCollection<long> Completed => CompletedQueue;

        public int MaxRunning => _maxRunning;

        public bool Throw { get; set; }

        public void Release(int count) => _gate.Release(count);

        public async Task RefreshAsync(long feedId, CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxRunning, running);
            try
            {
                await _gate.WaitAsync(cancellationToken);
                if (Throw)
                {
                    throw new InvalidOperationException("database exploded");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                CompletedQueue.Enqueue(feedId);
            }
        }

        public async Task WaitForRunningAsync(int count, CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _running) < count)
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        public async Task WaitForCompletedAsync(int count, CancellationToken cancellationToken)
        {
            while (CompletedQueue.Count < count)
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
