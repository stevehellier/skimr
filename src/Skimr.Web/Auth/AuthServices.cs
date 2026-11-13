using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Web.Components.Account;
using Skimr.Web.Configuration;

namespace Skimr.Web.Auth;

internal static class Policies
{
    public const string Admin = "Admin";

    /// <summary>Bearer token only: cookies are never accepted on /api (PLAN D33).</summary>
    public const string Api = "Api";

    public const string LoginRateLimit = "login";
    public const string ApiRateLimit = "api";
}

internal static class AuthServices
{
    public static IServiceCollection AddSkimrAuth(this IServiceCollection services, SkimrSettings settings)
    {
        services.AddIdentityCore<SkimrUser>(o =>
            {
                // Length over composition rules (NIST SP 800-63B), PLAN D34.
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.User.RequireUniqueEmail = false;
                o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@";
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<SkimrDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null);

        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "skimr.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            // Plain-HTTP LAN installs must work (PLAN D19); behind TLS the cookie is Secure.
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromDays(14);
            o.SlidingExpiration = true;
            o.LoginPath = "/login";
            o.AccessDeniedPath = "/access-denied";
        });

        // Disabling a user or resetting a password changes the stamp; cookies notice within a minute.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Admin, p => p.RequireRole(SkimrUser.AdminRole))
            .AddPolicy(Policies.Api, p => p.AddAuthenticationSchemes(ApiTokenAuthenticationHandler.SchemeName).RequireAuthenticatedUser());

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidatingAuthenticationStateProvider>();
        services.AddSingleton(new CircuitSessionOptions(TimeSpan.FromMinutes(1)));
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, SignedOutCircuitGuard>();

        services.AddSingleton(new AccountOptions(settings.DefaultRegistrationMode));
        services.AddScoped<AccountService>();
        services.AddScoped<ApiTokenService>();
        services.AddSingleton<SetupService>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Only form submissions count, so viewing the login page is never limited.
            o.AddPolicy(Policies.LoginRateLimit, context => HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.LoginAttemptsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })
                : RateLimitPartition.GetNoLimiter("read"));
            o.AddPolicy(Policies.ApiRateLimit, context => RateLimitPartition.GetTokenBucketLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ClientKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = settings.ApiRequestsPerMinute,
                    TokensPerPeriod = settings.ApiRequestsPerMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return services;
    }

    /// <summary>The client IP after forwarded headers. TestServer leaves it null, hence the fallback.</summary>
    private static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Until the first account exists, every page except the wizard redirects to it. Only page
    /// endpoints are affected: health checks, static files, WebSub and the API behave normally.
    /// </summary>
    public static IApplicationBuilder UseSetupRedirect(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var setup = context.RequestServices.GetRequiredService<SetupService>();
            // Status and error pages re-executed for a 404/429/500 must render, not redirect.
            var reExecuted = context.Features.Get<IStatusCodeReExecuteFeature>() is not null
                || context.Features.Get<IExceptionHandlerPathFeature>() is not null;
            if (!setup.IsComplete
                && !reExecuted
                && context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is { } page
                && page.Type != typeof(Setup))
            {
                context.Response.Redirect($"{context.Request.PathBase}/setup");
                return;
            }

            await next(context);
        });
}
