using Skimr.Infrastructure.Content;

namespace Skimr.Web.Hosting;

internal static class SecurityHeaders
{
    /// <summary>No inline script or style anywhere: the Blazor Server runtime doesn't need them.</summary>
    internal static string ContentSecurityPolicy(ContentOptions content)
    {
        var frames = content.AllowedEmbedHosts.Count == 0
            ? "'none'"
            : string.Join(' ', content.AllowedEmbedHosts.Select(h => "https://" + h));

        return "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https:; " +
            $"connect-src 'self'; frame-src {frames}; frame-ancestors 'none'; object-src 'none'; " +
            "base-uri 'self'; form-action 'self'";
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, ContentOptions content)
    {
        var policy = ContentSecurityPolicy(content);
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = policy;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }
}
