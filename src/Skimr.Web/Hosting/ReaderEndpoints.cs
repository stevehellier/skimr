using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Reading;
using Skimr.Web.Auth;

namespace Skimr.Web.Hosting;

/// <summary>Plain HTTP endpoints the reader pages link to. All need a signed-in user (fallback policy).</summary>
internal static class ReaderEndpoints
{
    public const string ImageRateLimit = "images";
    private const long MaxImageBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapReaderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/opml/export", async (ClaimsPrincipal user, OpmlService opml, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var xml = await opml.ExportAsync(userId, ct);
            return Results.File(Encoding.UTF8.GetBytes(xml), "text/x-opml", "skimr-subscriptions.opml");
        });

        // The image proxy (PLAN D54). Any failure is an empty 404: nothing from a non-image
        // response is ever passed on, and status pages aren't rendered for <img> requests.
        app.MapGet("/img/{signature}/{payload}", async (string signature, string payload, HttpContext context, ImageProxy proxy, IOutboundHttp http, CancellationToken ct) =>
        {
            if (!proxy.Enabled || proxy.Verify(signature, payload) is not { } original)
            {
                return Results.NotFound();
            }

            FetchResponse response;
            try
            {
                response = await http.GetAsync(new FetchRequest(new Uri(original), "image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8"), ct);
            }
            catch (FetchException)
            {
                return Results.NotFound();
            }

            if (!response.IsSuccess || response.Body.Length > MaxImageBytes || ImageBytes.Sniff(response.Body) is not { } mime)
            {
                return Results.NotFound();
            }

            return ImageResult(context, response.Body, mime);
        })
        .RequireRateLimiting(ImageRateLimit)
        .WithMetadata(new SkipStatusCodePagesAttribute());

        app.MapGet("/icons/{id:long}", async (long id, HttpContext context, SkimrDbContext db, StorageOptions storage, CancellationToken ct) =>
        {
            var icon = await db.Icons.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
            var path = icon is null ? null : Path.Combine(storage.IconDirectory, icon.FileName);
            if (icon is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "private, max-age=604800";
            return ImageResult(context, await File.ReadAllBytesAsync(path!, ct), icon.MimeType);
        })
        .WithMetadata(new SkipStatusCodePagesAttribute());

        return app;
    }

    /// <summary>Only safe headers: our own sniffed type, nosniff, a private cache, and a sandbox for SVG.</summary>
    private static IResult ImageResult(HttpContext context, byte[] bytes, string mime)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        if (string.IsNullOrEmpty(headers.CacheControl))
        {
            headers.CacheControl = "private, max-age=86400";
        }

        if (mime == ImageBytes.Svg)
        {
            headers.ContentSecurityPolicy = ImageBytes.SvgSecurityPolicy;
            context.Items[SecurityHeaders.OwnPolicyKey] = true;
        }

        return Results.Bytes(bytes, mime);
    }

    public static void AddImageRateLimit(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(ImageRateLimit, context => System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
            {
                // A busy magazine view loads dozens of images at once; this allows that, not scraping.
                TokenLimit = 600,
                TokensPerPeriod = 600,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
}
