namespace Skimr.Domain;

public sealed class Subscription
{
    public long Id { get; set; }

    // Foreign key to the user is added with Identity in Phase 3.
    public Guid UserId { get; set; }

    public long FeedId { get; set; }

    public Feed? Feed { get; set; }

    public string? CustomTitle { get; set; }

    /// <summary>Null means the configured default.</summary>
    public int? RefreshMinutes { get; set; }

    public bool AlwaysFetchFullText { get; set; }

    public DateTime CreatedAt { get; set; }
}
