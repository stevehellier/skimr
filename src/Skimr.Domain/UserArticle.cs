namespace Skimr.Domain;

/// <summary>
/// A user's state for one article. Created on ingest for every subscriber, so unread
/// counts are an index-only grouped count (PLAN D1).
/// </summary>
public sealed class UserArticle
{
    public Guid UserId { get; set; }

    public long ArticleId { get; set; }

    public Article? Article { get; set; }

    // Copied from the article so list and count queries stay on this table's indexes.
    public long FeedId { get; set; }

    public DateTime PublishedAt { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAt { get; set; }

    public bool IsSaved { get; set; }

    public DateTime? SavedAt { get; set; }
}
