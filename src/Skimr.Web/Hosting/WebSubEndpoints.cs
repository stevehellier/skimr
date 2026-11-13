using Microsoft.AspNetCore.Http.Features;
using Skimr.Infrastructure.Fetching;

namespace Skimr.Web.Hosting;

internal static class WebSubEndpoints
{
    /// <summary>Hub callbacks. Anonymous by design: deliveries are authenticated by HMAC signature.</summary>
    public static IEndpointRouteBuilder MapWebSub(this IEndpointRouteBuilder app, FetchOptions options)
    {
        var group = app.MapGroup(WebSub.CallbackPrefix.TrimEnd('/')).DisableAntiforgery();

        group.MapGet("/{feedId:long}", async (long feedId, HttpRequest request, WebSub webSub, CancellationToken ct) =>
        {
            var q = request.Query;
            var challenge = await webSub.VerifyAsync(feedId, q["hub.mode"], q["hub.topic"], q["hub.challenge"], q["hub.lease_seconds"], ct);
            return challenge is null ? Results.NotFound() : Results.Text(challenge, "text/plain");
        });

        group.MapPost("/{feedId:long}", async (long feedId, HttpContext context, WebSub webSub, CancellationToken ct) =>
        {
            var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false })
            {
                sizeLimit.MaxRequestBodySize = options.MaxResponseBytes;
            }

            if (context.Request.ContentLength > options.MaxResponseBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, ct);
            await webSub.DeliverAsync(feedId, buffer.ToArray(), context.Request.Headers["X-Hub-Signature"], ct);

            // Always 2xx, even when the signature is wrong: the spec says ignore it quietly.
            return Results.Accepted();
        });

        return app;
    }
}
