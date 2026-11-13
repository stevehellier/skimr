namespace Skimr.Domain;

public sealed class Subscription
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public long FeedId { get; set; }

    public Feed? Feed { get; set; }

    public string? CustomTitle { get; set; }

    /// <summary>Null means the configured default.</summary>
    public int? RefreshMinutes { get; set; }

    public bool AlwaysFetchFullText { get; set; }

    /// <summary>Order among sources that aren't in any folder.</summary>
    public int SortOrder { get; set; }

    public List<SubscriptionFolder> Folders { get; } = [];

    public DateTime CreatedAt { get; set; }
}
