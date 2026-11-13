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

    /// <summary>Plain text kept per article for search; enough for any normal post.</summary>
    public const int SearchTextLimit = 20_000;

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

        article.Guid = StoredText.Fit(item.Id, 2048);
        article.Url = url;
        article.Title = StoredText.Fit(item.Title.Length > 0 ? item.Title : HtmlText.Truncate(content.Snippet, 120), 1000) ?? "";
        if (article.Title.Length == 0)
        {
            article.Title = "(untitled)";
        }

        article.Author = StoredText.Fit(item.Author, 500);
        article.ContentHtml = StoredText.Fit(content.Html, int.MaxValue);
        article.Snippet = StoredText.Fit(content.Snippet, 500);
        article.SearchText = StoredText.Fit(HtmlText.ToPlainText(content.Html), SearchTextLimit);
        article.ThumbnailUrl = new[] { item.ThumbnailUrl, content.FirstImageUrl }.FirstOrDefault(u => u is { Length: <= 2048 });
        article.ContentHash = contentHash;
    }
}
