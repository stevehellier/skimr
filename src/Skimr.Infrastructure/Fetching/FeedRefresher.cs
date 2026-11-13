using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skimr.Domain;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Fetching;

/// <summary>An interface only because the scheduler tests need a fake.</summary>
internal interface IFeedRefresher
{
    Task RefreshAsync(long feedId, CancellationToken cancellationToken);
}

/// <summary>Fetches one feed, ingests it, records health, and schedules the next fetch.</summary>
internal sealed partial class FeedRefresher(
    SkimrDbContext db,
    IOutboundHttp http,
    ArticleIngester ingester,
    WebSub webSub,
    FetchOptions options,
    TimeProvider time,
    ILogger<FeedRefresher> logger) : IFeedRefresher
{
    public const string Accept =
        "application/rss+xml, application/atom+xml, application/feed+json, application/json;q=0.9, application/xml;q=0.8, text/xml;q=0.8, */*;q=0.5";

    public async Task RefreshAsync(long feedId, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.SingleOrDefaultAsync(f => f.Id == feedId, cancellationToken);
        if (feed is null)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var interval = RefreshSchedule.EffectiveInterval(
            await db.Subscriptions.Where(s => s.FeedId == feedId).Select(s => s.RefreshMinutes).ToListAsync(cancellationToken),
            options.DefaultIntervalMinutes);
        feed.LastFetchedAt = now;

        var succeeded = false;
        try
        {
            var response = await http.GetAsync(new FetchRequest(new Uri(feed.Url), Accept, feed.ETag, feed.LastModified), cancellationToken);

            if (response.Status == HttpStatusCode.NotModified)
            {
                await MarkStillPresentAsync(feed, now, cancellationToken);
                RecordSuccess(feed, now, interval, response);
                succeeded = true;
            }
            else if (!response.IsSuccess)
            {
                RecordFailure(feed, now, interval, $"HTTP {(int)response.Status} {response.Status}.", response.RetryAfter);
            }
            else
            {
                var parsed = FeedParser.Parse(response.Body, response.FinalUri);
                UpdateMetadata(feed, parsed, response);
                var added = await ingester.IngestAsync(feed, parsed, response.FinalUri, now, cancellationToken);
                feed.ETag = ArticleIngester.Limit(response.ETag, 500);
                feed.LastModified = ArticleIngester.Limit(response.LastModified, 100);
                RecordSuccess(feed, now, interval, response);
                LogFetched(feed.Id, added);
                succeeded = true;
            }
        }
        catch (Exception ex) when (ex is FetchException or FeedParseException)
        {
            RecordFailure(feed, now, interval, ex.Message, retryAfter: null);
        }

        if (succeeded)
        {
            await webSub.EnsureSubscribedAsync(feed, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A 304 means the document is unchanged, so everything seen at the last successful fetch
    /// is still in it. Without this, retention would treat those items as gone (PLAN D3).
    /// </summary>
    private async Task MarkStillPresentAsync(Feed feed, DateTime now, CancellationToken cancellationToken)
    {
        if (feed.LastSuccessAt is not { } lastSuccess)
        {
            return;
        }

        await db.Articles
            .Where(a => a.FeedId == feed.Id && a.LastSeenAt >= lastSuccess)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, now), cancellationToken);
    }

    private static void UpdateMetadata(Feed feed, ParsedFeed parsed, FetchResponse response)
    {
        feed.Title = ArticleIngester.Limit(parsed.Title, 500) ?? feed.Title;
        feed.SiteUrl = ArticleIngester.Limit(parsed.SiteUrl, 2048) ?? feed.SiteUrl;
        feed.Description = ArticleIngester.Limit(parsed.Description, 2000) ?? feed.Description;
        feed.HubUrl = ArticleIngester.Limit(response.HubUrl ?? parsed.HubUrl, 2048);
        feed.SelfUrl = ArticleIngester.Limit(response.SelfUrl ?? parsed.SelfUrl, 2048);
    }

    private static void RecordSuccess(Feed feed, DateTime now, TimeSpan interval, FetchResponse response)
    {
        feed.LastSuccessAt = now;
        feed.ConsecutiveFailures = 0;
        feed.NextFetchAt = RefreshSchedule.Next(now, interval, 0, response.MaxAge, response.RetryAfter, Jitter());
    }

    private void RecordFailure(Feed feed, DateTime now, TimeSpan interval, string error, TimeSpan? retryAfter)
    {
        feed.LastErrorAt = now;
        feed.LastError = ArticleIngester.Limit(error, 1000);
        feed.ConsecutiveFailures++;
        feed.NextFetchAt = RefreshSchedule.Next(now, interval, feed.ConsecutiveFailures, null, retryAfter, Jitter());
        LogFailed(feed.Id, feed.ConsecutiveFailures, error);
    }

    private static double Jitter() => Random.Shared.NextDouble() * 2 - 1;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Feed {FeedId} fetched: {Added} new items")]
    private partial void LogFetched(long feedId, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {FeedId} failed ({Failures} in a row): {Error}")]
    private partial void LogFailed(long feedId, int failures, string error);
}
