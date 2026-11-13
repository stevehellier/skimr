namespace Skimr.Domain;

/// <summary>A user's folder ("Feed" in Feedly). A subscription can be in several folders.</summary>
public sealed class Folder
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public List<SubscriptionFolder> Subscriptions { get; } = [];
}

public sealed class SubscriptionFolder
{
    public long SubscriptionId { get; set; }

    public Subscription? Subscription { get; set; }

    public long FolderId { get; set; }

    public Folder? Folder { get; set; }

    public int SortOrder { get; set; }
}
