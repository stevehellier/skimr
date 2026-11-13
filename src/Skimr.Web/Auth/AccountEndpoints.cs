using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Skimr.Infrastructure.Identity;

namespace Skimr.Web.Auth;

internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromForm] makes the endpoint validate the antiforgery token, so a third-party page can't sign you out.
        app.MapPost("/logout", async (SignInManager<SkimrUser> signIn, [FromForm] string? returnUrl) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("~/login");
        });

        return app;
    }

    public sealed record TokenRequest(string? Name, int? ExpiresInDays);

    /// <summary>Bearer-token API (PLAN D33). Phase 6 fills in the rest of the surface.</summary>
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(Policies.Api)
            .RequireRateLimiting(Policies.ApiRateLimit)
            .DisableAntiforgery()
            .WithMetadata(new SkipStatusCodePagesAttribute());

        api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            id = UserId(user),
            userName = user.Identity?.Name,
            isAdmin = user.IsInRole(SkimrUser.AdminRole),
        }));

        api.MapGet("/tokens", async (ClaimsPrincipal user, ApiTokenService tokens, CancellationToken ct) =>
            (await tokens.ListAsync(UserId(user), ct)).Select(t => new { t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.ExpiresAt }));

        api.MapPost("/tokens", async (TokenRequest request, ClaimsPrincipal user, ApiTokenService tokens, CancellationToken ct) =>
        {
            if (request.ExpiresInDays is < 1 or > 3650)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["expiresInDays"] = ["Must be between 1 and 3650, or omitted."] });
            }

            var created = await tokens.CreateAsync(UserId(user), request.Name ?? "API token", request.ExpiresInDays, ct);
            var t = created.Token;
            return Results.Created($"/api/v1/tokens/{t.Id}", new { t.Id, t.Name, t.Prefix, t.CreatedAt, t.ExpiresAt, token = created.PlainText });
        });

        api.MapDelete("/tokens/{id:long}", async (long id, ClaimsPrincipal user, ApiTokenService tokens, CancellationToken ct) =>
            await tokens.RevokeAsync(UserId(user), id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
