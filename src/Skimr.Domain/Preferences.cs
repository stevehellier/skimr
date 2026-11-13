namespace Skimr.Domain;

public enum ArticleLayout
{
    TitleOnly,
    Magazine,
    Cards,
}

public enum ArticleSort
{
    Newest,
    Oldest,
}

public enum MarkReadMode
{
    /// <summary>Opening an article marks it read.</summary>
    OnOpen,

    /// <summary>Scrolling past an article in the list marks it read (opening does too).</summary>
    OnScroll,

    /// <summary>Only the "mark read" action does.</summary>
    Manual,
}

public sealed class UserPreferences
{
    public Guid UserId { get; set; }

    public MarkReadMode MarkReadMode { get; set; } = MarkReadMode.OnOpen;

    /// <summary>The user's consent to send article text to the admin's AI provider (PLAN D12). Off by default.</summary>
    public bool AiEnabled { get; set; }
}

/// <summary>Remembered per view ("all", "today", "saved", "folder:12", "feed:34").</summary>
public sealed class ViewPreference
{
    public Guid UserId { get; set; }

    public required string ViewKey { get; set; }

    public ArticleLayout Layout { get; set; } = ArticleLayout.Magazine;

    public ArticleSort Sort { get; set; } = ArticleSort.Newest;

    public bool UnreadOnly { get; set; } = true;
}
