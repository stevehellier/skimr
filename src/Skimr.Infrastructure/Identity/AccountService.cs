using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Identity;

internal sealed record AccountResult(bool Succeeded, IReadOnlyList<string> Errors, Guid? UserId = null)
{
    public static AccountResult Ok(Guid? userId = null) => new(true, [], userId);

    public static AccountResult Fail(params string[] errors) => new(false, errors);

    public static AccountResult Fail(IdentityResult result) => new(false, result.Errors.Select(e => e.Description).ToList());
}

internal sealed record UserSummary(Guid Id, string UserName, bool IsAdmin, bool IsDisabled, bool IsLockedOut, DateTime CreatedAt);

internal sealed record AccountOptions(RegistrationMode DefaultRegistrationMode);

/// <summary>Sign-up, invites and admin user management.</summary>
internal sealed class AccountService(
    SkimrDbContext db,
    UserManager<SkimrUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    AccountOptions options,
    TimeProvider time)
{
    public const string InvitePrefix = "inv_";
    private const string RegistrationModeKey = "RegistrationMode";
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    // ---- Registration mode -------------------------------------------------------------

    public async Task<RegistrationMode> GetRegistrationModeAsync(CancellationToken cancellationToken)
    {
        var stored = await db.AppSettings.Where(s => s.Key == RegistrationModeKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);
        return Enum.TryParse<RegistrationMode>(stored, out var mode) && Enum.IsDefined(mode) ? mode : options.DefaultRegistrationMode;
    }

    public async Task SetRegistrationModeAsync(RegistrationMode mode, CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == RegistrationModeKey, cancellationToken);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = RegistrationModeKey, Value = mode.ToString() });
        }
        else
        {
            setting.Value = mode.ToString();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Invites -----------------------------------------------------------------------

    public async Task<string> CreateInviteAsync(Guid createdBy, string? note, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var code = Secrets.NewToken(InvitePrefix);
        db.Invites.Add(new Invite
        {
            TokenHash = Secrets.Hash(code),
            Note = StoredText.Fit(string.IsNullOrWhiteSpace(note) ? null : note.Trim(), 200),
            CreatedByUserId = createdBy,
            CreatedAt = now,
            ExpiresAt = now + InviteLifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return code;
    }

    public Task<List<Invite>> ListPendingInvitesAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return db.Invites.Where(i => i.UsedAt == null && i.ExpiresAt > now).OrderByDescending(i => i.CreatedAt).AsNoTracking().ToListAsync(cancellationToken);
    }

    public Task RevokeInviteAsync(long id, CancellationToken cancellationToken) =>
        db.Invites.Where(i => i.Id == id && i.UsedAt == null).ExecuteDeleteAsync(cancellationToken);

    // ---- Sign-up -----------------------------------------------------------------------

    public async Task<AccountResult> RegisterAsync(string userName, string password, string? inviteCode, CancellationToken cancellationToken)
    {
        var mode = await GetRegistrationModeAsync(cancellationToken);
        if (mode == RegistrationMode.Closed)
        {
            return AccountResult.Fail("Registration is closed. Ask an admin to create an account for you.");
        }

        var needsInvite = mode == RegistrationMode.InviteOnly;
        if (needsInvite && string.IsNullOrWhiteSpace(inviteCode))
        {
            return AccountResult.Fail("You need an invite link to sign up.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inviteHash = needsInvite ? Secrets.Hash(inviteCode!.Trim()) : null;
        if (inviteHash is not null)
        {
            // Claim the invite first, conditionally: of two simultaneous sign-ups, only one updates the row.
            var now = time.GetUtcNow().UtcDateTime;
            var claimed = await db.Invites
                .Where(i => i.TokenHash == inviteHash && i.UsedAt == null && i.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now), cancellationToken);
            if (claimed == 0)
            {
                return AccountResult.Fail("This invite link is invalid, already used or expired.");
            }
        }

        var user = NewUser(userName);
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            // Disposing without commit rolls the invite claim back, so the link still works.
            return AccountResult.Fail(created);
        }

        if (inviteHash is not null)
        {
            await db.Invites.Where(i => i.TokenHash == inviteHash)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedByUserId, user.Id), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return AccountResult.Ok(user.Id);
    }

    public async Task<AccountResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountResult.Fail("Account not found.");
        }

        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        return result.Succeeded ? AccountResult.Ok(userId) : AccountResult.Fail(result);
    }

    // ---- Admin -------------------------------------------------------------------------

    public async Task<List<UserSummary>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var admins = await AdminIdsAsync(cancellationToken);
        var all = await users.Users.OrderBy(u => u.UserName).AsNoTracking().ToListAsync(cancellationToken);
        var now = time.GetUtcNow();

        // LockoutEnd is a DateTimeOffset, which SQLite can't compare in SQL (PLAN D2): check in memory.
        return all.Select(u => new UserSummary(
            u.Id,
            u.UserName ?? "",
            admins.Contains(u.Id),
            IsDisabled(u),
            !IsDisabled(u) && u.LockoutEnd > now,
            u.CreatedAt)).ToList();
    }

    public async Task<AccountResult> CreateUserAsync(string userName, string password, bool isAdmin, CancellationToken cancellationToken)
    {
        var user = NewUser(userName);
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return AccountResult.Fail(created);
        }

        if (isAdmin)
        {
            await EnsureAdminRoleAsync();
            await users.AddToRoleAsync(user, SkimrUser.AdminRole);
        }

        return AccountResult.Ok(user.Id);
    }

    public async Task<AccountResult> SetDisabledAsync(Guid actorId, Guid userId, bool disabled, CancellationToken cancellationToken)
    {
        var (user, error) = await FindTargetAsync(actorId, userId, removesAdmin: disabled, cancellationToken);
        if (user is null)
        {
            return AccountResult.Fail(error!);
        }

        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, disabled ? DateTimeOffset.MaxValue : null);
        // Signs the user out everywhere: cookies are revalidated against the stamp.
        await users.UpdateSecurityStampAsync(user);
        return AccountResult.Ok(userId);
    }

    public async Task<AccountResult> DeleteAsync(Guid actorId, Guid userId, CancellationToken cancellationToken)
    {
        var (user, error) = await FindTargetAsync(actorId, userId, removesAdmin: true, cancellationToken);
        if (user is null)
        {
            return AccountResult.Fail(error!);
        }

        var result = await users.DeleteAsync(user);
        return result.Succeeded ? AccountResult.Ok(userId) : AccountResult.Fail(result);
    }

    public async Task<AccountResult> SetAdminAsync(Guid actorId, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var (user, error) = await FindTargetAsync(actorId, userId, removesAdmin: !isAdmin, cancellationToken);
        if (user is null)
        {
            return AccountResult.Fail(error!);
        }

        await EnsureAdminRoleAsync();
        var result = isAdmin
            ? await users.AddToRoleAsync(user, SkimrUser.AdminRole)
            : await users.RemoveFromRoleAsync(user, SkimrUser.AdminRole);
        if (!result.Succeeded && !result.Errors.Any(e => e.Code is "UserAlreadyInRole" or "UserNotInRole"))
        {
            return AccountResult.Fail(result);
        }

        await users.UpdateSecurityStampAsync(user);
        return AccountResult.Ok(userId);
    }

    public async Task<AccountResult> ResetPasswordAsync(Guid userId, string newPassword)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountResult.Fail("Account not found.");
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        return result.Succeeded ? AccountResult.Ok(userId) : AccountResult.Fail(result);
    }

    private async Task<(SkimrUser? User, string? Error)> FindTargetAsync(Guid actorId, Guid userId, bool removesAdmin, CancellationToken cancellationToken)
    {
        if (actorId == userId)
        {
            return (null, "You can't do that to your own account.");
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return (null, "Account not found.");
        }

        if (removesAdmin && await users.IsInRoleAsync(user, SkimrUser.AdminRole))
        {
            var activeAdmins = (await ListUsersAsync(cancellationToken)).Count(u => u.IsAdmin && !u.IsDisabled);
            if (activeAdmins <= 1 && !IsDisabled(user))
            {
                return (null, "That's the last active admin. Make someone else an admin first.");
            }
        }

        return (user, null);
    }

    private async Task<HashSet<Guid>> AdminIdsAsync(CancellationToken cancellationToken)
    {
        var roleId = await db.Roles.Where(r => r.NormalizedName == SkimrUser.AdminRole.ToUpperInvariant()).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken);
        return roleId is null
            ? []
            : (await db.UserRoles.Where(ur => ur.RoleId == roleId).Select(ur => ur.UserId).ToListAsync(cancellationToken)).ToHashSet();
    }

    private async Task EnsureAdminRoleAsync()
    {
        if (!await roles.RoleExistsAsync(SkimrUser.AdminRole))
        {
            await roles.CreateAsync(new IdentityRole<Guid>(SkimrUser.AdminRole));
        }
    }

    private SkimrUser NewUser(string userName) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = userName.Trim(),
        CreatedAt = time.GetUtcNow().UtcDateTime,
    };

    internal static bool IsDisabled(SkimrUser user) => user.LockoutEnd == DateTimeOffset.MaxValue;
}
