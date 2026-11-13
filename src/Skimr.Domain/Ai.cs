namespace Skimr.Domain;

/// <summary>A user's filter: articles from chosen sources that match a plain-English rule (PLAN D62).</summary>
public sealed class AiFeed
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public required string Rule { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Highest article id already checked; only newer articles are sent to the model.</summary>
    public long LastCheckedArticleId { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Why the last check failed, shown to the user (e.g. the daily limit).</summary>
    public string? LastError { get; set; }

    public List<AiFeedSource> Sources { get; } = [];
}

/// <summary>One watched folder or subscription; exactly one of the two ids is set.</summary>
public sealed class AiFeedSource
{
    public long Id { get; set; }

    public long AiFeedId { get; set; }

    public long? FolderId { get; set; }

    public long? SubscriptionId { get; set; }
}

public sealed class AiFeedMatch
{
    public long AiFeedId { get; set; }

    public AiFeed? AiFeed { get; set; }

    public long ArticleId { get; set; }

    public DateTime MatchedAt { get; set; }
}

/// <summary>A generated summary. Articles are global, so one summary per model serves every user.</summary>
public sealed class ArticleSummary
{
    public long ArticleId { get; set; }

    public required string Model { get; set; }

    public required string Text { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Model calls a user caused on one UTC day, for the daily cap.</summary>
public sealed class AiUsage
{
    public Guid UserId { get; set; }

    public DateTime Day { get; set; }

    public int Calls { get; set; }
}
