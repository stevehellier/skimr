using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;

namespace Skimr.IntegrationTests.Infrastructure;

internal static class TestData
{
    /// <summary>A bare user row for tests that only need the foreign key to exist.</summary>
    public static async Task<Guid> AddUserAsync(SkimrDbContext db, CancellationToken cancellationToken)
    {
        var name = "user-" + Guid.NewGuid().ToString("N")[..8];
        var user = new SkimrUser
        {
            Id = Guid.NewGuid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }
}
