namespace Skimr.Domain;

/// <summary>A single-use sign-up link. Only the SHA-256 of the invite code is stored.</summary>
public sealed class Invite
{
    public long Id { get; set; }

    public required string TokenHash { get; set; }

    public string? Note { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public Guid? UsedByUserId { get; set; }
}
