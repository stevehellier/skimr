using System.Security.Claims;
using System.Text;
using Skimr.Infrastructure.Reading;

namespace Skimr.Web.Hosting;

/// <summary>Plain HTTP endpoints the reader pages link to. All need a signed-in user (fallback policy).</summary>
internal static class ReaderEndpoints
{
    public static IEndpointRouteBuilder MapReaderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/opml/export", async (ClaimsPrincipal user, OpmlService opml, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var xml = await opml.ExportAsync(userId, ct);
            return Results.File(Encoding.UTF8.GetBytes(xml), "text/x-opml", "skimr-subscriptions.opml");
        });

        return app;
    }
}
