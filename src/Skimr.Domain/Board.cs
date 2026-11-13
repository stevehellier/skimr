namespace Skimr.Domain;

/// <summary>A user's collection of articles. An article can sit in several boards.</summary>
public sealed class Board
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public List<BoardArticle> Articles { get; } = [];
}

public sealed class BoardArticle
{
    public long BoardId { get; set; }

    public Board? Board { get; set; }

    public long ArticleId { get; set; }

    public DateTime AddedAt { get; set; }
}
