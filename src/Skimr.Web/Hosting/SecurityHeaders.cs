using Skimr.Infrastructure.Content;

namespace Skimr.Web.Hosting;

internal static class SecurityHeaders
{
    /// <summary>Set in HttpContext.Items by an endpoint that sends its own (stricter) CSP.</summary>
    public const string OwnPolicyKey = "Skimr.OwnContentSecurityPolicy";

    /// <summary>No inline script or style anywhere: the Blazor Server runtime doesn't need them.</summary>
    internal static string ContentSecurityPolicy(ContentOptions content)
    {
        var frames = content.AllowedEmbedHosts.Count == 0
            ? "'none'"
            : string.Join(' ', content.AllowedEmbedHosts.Select(h => "https://" + h));

        // With the proxy every article image is same-origin; without it images load from their sites.
        var images = content.ImageProxyEnabled ? "'self' data:" : "'self' data: https:";

        return "default-src 'self'; script-src 'self'; style-src 'self'; " +
            $"img-src {images}; " +
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
                // Always ours: Blazor sets a weaker frame-ancestors-only policy on component pages.
                // The one exception is an endpoint that opted in to a stricter policy of its own.
                if (!context.Items.ContainsKey(OwnPolicyKey))
                {
                    headers.ContentSecurityPolicy = policy;
                }
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }
}
