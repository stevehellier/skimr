using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Fetching;

/// <summary>
/// Stores new items and updates edited ones, and fans new items out to every subscriber.
/// Used by polling and by WebSub pushes. A push carries only some items, so this only ever
/// touches the items it's given. Changes are left for the caller to save.
/// </summary>
internal sealed class ArticleIngester(SkimrDbContext db, ContentSanitizer sanitizer)
{
    private const int MaxItemsPerFetch = 500;

    public async Task<int> IngestAsync(Feed feed, ParsedFeed parsed, Uri documentUri, DateTime now, CancellationToken cancellationToken)
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

    internal static string? Limit(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
