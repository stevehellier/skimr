namespace Skimr.Domain;

/// <summary>A source URL. Global: fetched once however many users subscribe.</summary>
public sealed class Feed
{
    public long Id { get; set; }

    public required string Url { get; set; }

    public string? SiteUrl { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    // Conditional GET validators, stored exactly as the server sent them.
    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastFetchedAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    public DateTime? LastErrorAt { get; set; }

    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTime NextFetchAt { get; set; }

    // WebSub
    public string? HubUrl { get; set; }

    public string? SelfUrl { get; set; }

    public string? WebSubSecret { get; set; }

    public DateTime? WebSubLeaseExpiresAt { get; set; }

    public long? IconId { get; set; }

    /// <summary>Last favicon attempt, so a site without one isn't asked on every fetch.</summary>
    public DateTime? IconCheckedAt { get; set; }

    public List<Subscription> Subscriptions { get; } = [];
}
