using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Skimr.Infrastructure.Identity;

namespace Skimr.Web.Auth;

/// <summary>How often a live circuit re-checks its user. Tests shorten it.</summary>
internal sealed record CircuitSessionOptions(TimeSpan RevalidationInterval);

/// <summary>
/// Re-checks the security stamp of a live Blazor circuit every minute, so disabling a user or
/// resetting their password also ends sessions that are already open.
/// </summary>
internal sealed class RevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopes,
    IOptions<IdentityOptions> options,
    CircuitSessionOptions session)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => session.RevalidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SkimrUser>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null || AccountService.IsDisabled(user))
        {
            return false;
        }

        if (!users.SupportsUserSecurityStamp)
        {
            return true;
        }

        var principalStamp = state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await users.GetSecurityStampAsync(user);
    }
}
