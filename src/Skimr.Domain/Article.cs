namespace Skimr.Domain;

/// <summary>One item from a feed. Global; per-user state lives in <see cref="UserArticle"/>.</summary>
public sealed class Article
{
    public long Id { get; set; }

    public long FeedId { get; set; }

    public Feed? Feed { get; set; }

    /// <summary>SHA-256 hex of the item identity (GUID, else link, else title + date). Unique per feed.</summary>
    public required string DedupKey { get; set; }

    public string? Guid { get; set; }

    public string? Url { get; set; }

    /// <summary>Plain text, never HTML.</summary>
    public required string Title { get; set; }

    /// <summary>Plain text, never HTML.</summary>
    public string? Author { get; set; }

    /// <summary>Sanitised HTML.</summary>
    public string? ContentHtml { get; set; }

    /// <summary>Plain-text excerpt of the content for list views.</summary>
    public string? Snippet { get; set; }

    /// <summary>
    /// Plain text of the content (no tags), capped, for the search index. Indexing HTML would
    /// let searches match tag and attribute names.
    /// </summary>
    public string? SearchText { get; set; }

    public string? ThumbnailUrl { get; set; }

    public DateTime PublishedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>SHA-256 hex of title + content; a change means the item was edited upstream.</summary>
    public required string ContentHash { get; set; }

    public DateTime FetchedAt { get; set; }

    /// <summary>Last time the item was present in the feed. Retention keeps articles still being served.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>Sanitised HTML from full-text extraction.</summary>
    public string? FullTextHtml { get; set; }

    public DateTime? FullTextFetchedAt { get; set; }
}
