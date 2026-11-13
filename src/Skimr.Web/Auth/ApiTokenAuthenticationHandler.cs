using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Skimr.Infrastructure.Identity;

namespace Skimr.Web.Auth;

/// <summary><c>Authorization: Bearer skimr_…</c>. The only way into /api (PLAN D33).</summary>
internal sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens,
    UserManager<SkimrUser> users,
    IUserClaimsPrincipalFactory<SkimrUser> principals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string TokenIdClaim = "skimr:token_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = await tokens.ValidateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("Invalid or expired token.");
        }

        var user = await users.FindByIdAsync(token.Value.UserId.ToString());
        // A disabled account's tokens stop working immediately. A temporary lockout from wrong
        // passwords doesn't affect them: otherwise anyone knowing a user name could block their apps.
        if (user is null || AccountService.IsDisabled(user))
        {
            return AuthenticateResult.Fail("Account is disabled.");
        }

        var principal = await principals.CreateAsync(user);
        var identity = new ClaimsIdentity(principal.Claims, SchemeName);
        identity.AddClaim(new Claim(TokenIdClaim, token.Value.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
