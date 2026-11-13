using Microsoft.AspNetCore.Identity;

namespace Skimr.Infrastructure.Identity;

/// <summary>An account. Users sign in with a user name; email is optional and unused.</summary>
public sealed class SkimrUser : IdentityUser<Guid>
{
    public const string AdminRole = "Admin";

    public DateTime CreatedAt { get; set; }
}
