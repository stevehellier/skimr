namespace Skimr.Domain;

/// <summary>A personal access token. Only the SHA-256 of the token is stored; the token itself is shown once.</summary>
public sealed class ApiToken
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>First characters of the token, so users can tell their tokens apart.</summary>
    public required string Prefix { get; set; }

    public required string TokenHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
