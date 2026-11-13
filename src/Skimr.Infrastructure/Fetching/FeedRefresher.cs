using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skimr.Domain;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Fetching;

/// <summary>An interface only because the scheduler tests need a fake.</summary>
internal interface IFeedRefresher
{
    Task RefreshAsync(long feedId, CancellationToken cancellationToken);
}

/// <summary>Fetches one feed, stores new and changed items, fans them out to subscribers, and schedules the next fetch.</summary>
internal sealed partial class FeedRefresher(
    SkimrDbContext db,
    IOutboundHttp http,
    ContentSanitizer sanitizer,
    FetchOptions options,
    TimeProvider time,
    ILogger<FeedRefresher> logger) : IFeedRefresher
{
    public const string Accept =
        "application/rss+xml, application/atom+xml, application/feed+json, application/json;q=0.9, application/xml;q=0.8, text/xml;q=0.8, */*;q=0.5";

    private const int MaxItemsPerFetch = 500;

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

        try
        {
            var response = await http.GetAsync(new FetchRequest(new Uri(feed.Url), Accept, feed.ETag, feed.LastModified), cancellationToken);

            if (response.Status == HttpStatusCode.NotModified)
            {
                await MarkStillPresentAsync(feed, now, cancellationToken);
                RecordSuccess(feed, now, interval, response);
            }
            else if (!response.IsSuccess)
            {
                RecordFailure(feed, now, interval, $"HTTP {(int)response.Status} {response.Status}.", response.RetryAfter);
            }
            else
            {
                var parsed = FeedParser.Parse(response.Body, response.FinalUri);
                UpdateMetadata(feed, parsed, response);
                var added = await IngestAsync(feed, parsed, response.FinalUri, now, cancellationToken);
                feed.ETag = Limit(response.ETag, 500);
                feed.LastModified = Limit(response.LastModified, 100);
                RecordSuccess(feed, now, interval, response);
                LogFetched(feed.Id, added);
            }
        }
        catch (Exception ex) when (ex is FetchException or FeedParseException)
        {
            RecordFailure(feed, now, interval, ex.Message, retryAfter: null);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stores new items and updates edited ones. Also used for WebSub pushes, which carry only
    /// some items, so it only ever touches the items it's given.
    /// </summary>
    internal async Task<int> IngestAsync(Feed feed, ParsedFeed parsed, Uri documentUri, DateTime now, CancellationToken cancellationToken)
    {
        var candidates = parsed.Items
            .Take(MaxItemsPerFetch)
            .Select(item => (Item: item, Key: ArticleIdentity.DedupKey(item.Id, item.Url, item.Title, item.Published)))
            .DistinctBy(c => c.Key)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var keys = candidates.Select(c => c.Key).ToList();
        var existing = await db.Articles
            .Where(a => a.FeedId == feed.Id && keys.Contains(a.DedupKey))
            .ToDictionaryAsync(a => a.DedupKey, cancellationToken);
        var subscribers = await db.Subscriptions
            .Where(s => s.FeedId == feed.Id)
            .Select(s => s.UserId)
            .ToListAsync(cancellationToken);

        var fallbackBase = feed.SiteUrl ?? documentUri.AbsoluteUri;
        var added = 0;
        foreach (var (item, key) in candidates)
        {
            var contentHash = ArticleIdentity.Hash(item.Title + "\0" + item.ContentHtml);
            if (existing.TryGetValue(key, out var article))
            {
                article.LastSeenAt = now;
                if (article.ContentHash != contentHash)
                {
                    // Edited upstream: refresh the content, keep everyone's read/saved state.
                    Apply(article, item, contentHash, fallbackBase);
                    article.UpdatedAt = item.Updated ?? now;
                }

                continue;
            }

            article = new Article
            {
                FeedId = feed.Id,
                DedupKey = key,
                Title = "",
                ContentHash = contentHash,
                // Undated items get the time we first saw them; future dates are clamped to now.
                PublishedAt = item.Published is { } published && published < now ? published : now,
                UpdatedAt = item.Updated,
                FetchedAt = now,
                LastSeenAt = now,
            };
            Apply(article, item, contentHash, fallbackBase);
            db.Articles.Add(article);

            foreach (var userId in subscribers)
            {
                db.UserArticles.Add(new UserArticle
                {
                    UserId = userId,
                    Article = article,
                    FeedId = feed.Id,
                    PublishedAt = article.PublishedAt,
                });
            }

            added++;
        }

        return added;
    }

    private void Apply(Article article, ParsedItem item, string contentHash, string fallbackBase)
    {
        var url = item.Url is { Length: <= 2048 } ? item.Url : null;
        var content = sanitizer.Sanitize(item.ContentHtml, url ?? fallbackBase);

        article.Guid = Limit(item.Id, 2048);
        article.Url = url;
        article.Title = Limit(item.Title.Length > 0 ? item.Title : HtmlText.Truncate(content.Snippet, 120), 1000) ?? "";
        if (article.Title.Length == 0)
        {
            article.Title = "(untitled)";
        }

        article.Author = Limit(item.Author, 500);
        article.ContentHtml = content.Html;
        article.Snippet = content.Snippet;
        article.ThumbnailUrl = new[] { item.ThumbnailUrl, content.FirstImageUrl }.FirstOrDefault(u => u is { Length: <= 2048 });
        article.ContentHash = contentHash;
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
        feed.Title = Limit(parsed.Title, 500) ?? feed.Title;
        feed.SiteUrl = Limit(parsed.SiteUrl, 2048) ?? feed.SiteUrl;
        feed.Description = Limit(parsed.Description, 2000) ?? feed.Description;
        feed.HubUrl = Limit(response.HubUrl ?? parsed.HubUrl, 2048);
        feed.SelfUrl = Limit(response.SelfUrl ?? parsed.SelfUrl, 2048);
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
        feed.LastError = Limit(error, 1000);
        feed.ConsecutiveFailures++;
        feed.NextFetchAt = RefreshSchedule.Next(now, interval, feed.ConsecutiveFailures, null, retryAfter, Jitter());
        LogFailed(feed.Id, feed.ConsecutiveFailures, error);
    }

    private static double Jitter() => Random.Shared.NextDouble() * 2 - 1;

    private static string? Limit(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Debug, Message = "Feed {FeedId} fetched: {Added} new items")]
    private partial void LogFetched(long feedId, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {FeedId} failed ({Failures} in a row): {Error}")]
    private partial void LogFailed(long feedId, int failures, string error);
}
