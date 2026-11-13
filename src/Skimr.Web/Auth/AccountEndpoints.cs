using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Skimr.Infrastructure.Identity;

namespace Skimr.Web.Auth;

internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Checked explicitly so a third-party page can't sign you out (and a bad token is a 400, not a 500).
        app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<SkimrUser> signIn) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            await signIn.SignOutAsync();
            return Results.LocalRedirect("~/login");
        }).DisableAntiforgery();

        return app;
    }
}
