using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Identity;

internal sealed record CreatedToken(ApiToken Token, string PlainText);

/// <summary>Personal access tokens. Only the hash is stored (PLAN D7); the plain token is returned once.</summary>
internal sealed class ApiTokenService(SkimrDbContext db, TimeProvider time)
{
    public const string Prefix = "skimr_";

    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    public async Task<CreatedToken> CreateAsync(Guid userId, string name, int? expiresInDays, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var plain = Secrets.NewToken(Prefix);
        var token = new ApiToken
        {
            UserId = userId,
            Name = StoredText.Fit(string.IsNullOrWhiteSpace(name) ? "Token" : name.Trim(), 100)!,
            Prefix = plain[..12],
            TokenHash = Secrets.Hash(plain),
            CreatedAt = now,
            ExpiresAt = expiresInDays is > 0 ? now.AddDays(expiresInDays.Value) : null,
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedToken(token, plain);
    }

    public Task<List<ApiToken>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        db.ApiTokens.Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt).AsNoTracking().ToListAsync(cancellationToken);

    /// <returns>False if the token doesn't exist or belongs to someone else.</returns>
    public async Task<bool> RevokeAsync(Guid userId, long tokenId, CancellationToken cancellationToken) =>
        await db.ApiTokens.Where(t => t.Id == tokenId && t.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0;

    /// <summary>The token's owner, if the token exists and hasn't expired. Records when it was last used.</summary>
    public async Task<(Guid UserId, long TokenId)?> ValidateAsync(string plain, CancellationToken cancellationToken)
    {
        if (!plain.StartsWith(Prefix, StringComparison.Ordinal) || plain.Length > 100)
        {
            return null;
        }

        var hash = Secrets.Hash(plain);
        var token = await db.ApiTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.UserId, t.ExpiresAt, t.LastUsedAt })
            .SingleOrDefaultAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        if (token is null || token.ExpiresAt <= now)
        {
            return null;
        }

        // Coarse on purpose: a write per request would make every API call a database write.
        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedResolution)
        {
            await db.ApiTokens.Where(t => t.Id == token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), cancellationToken);
        }

        return (token.UserId, token.Id);
    }
}
