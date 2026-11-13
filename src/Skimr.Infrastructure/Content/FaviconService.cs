using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Content;

/// <summary>Where persistent files live (PLAN: everything under one data directory).</summary>
public sealed record StorageOptions(string DataDirectory)
{
    public string IconDirectory => Path.Combine(DataDirectory, "icons");
}

/// <summary>
/// Finds, checks and caches a site's favicon, once per feed (not per subscriber), after a
/// successful fetch and outside the ingest transaction. Icons are served from the app, so the
/// reader's browser never contacts the site. Identical icons are stored once.
/// </summary>
internal sealed class FaviconService(SkimrDbContext db, IOutboundHttp http, StorageOptions storage, TimeProvider time)
{
    public const int MaxBytes = 200 * 1024;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);

    private static readonly HtmlParser Parser = new();

    public static bool IsDue(Feed feed, DateTime now) => feed.IconCheckedAt is not { } checkedAt || now - checkedAt > RetryAfter;

    public async Task UpdateAsync(long feedId, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.SingleOrDefaultAsync(f => f.Id == feedId, cancellationToken);
        if (feed is null)
        {
            return;
        }

        var site = Uri.TryCreate(feed.SiteUrl ?? feed.Url, UriKind.Absolute, out var siteUri) ? siteUri : null;
        if (site is not null)
        {
            foreach (var candidate in await CandidatesAsync(site, cancellationToken))
            {
                if (await TryStoreAsync(candidate, cancellationToken) is { } iconId)
                {
                    feed.IconId = iconId;
                    break;
                }
            }
        }

        feed.IconCheckedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Icons the site's home page declares, then /favicon.ico.</summary>
    private async Task<List<Uri>> CandidatesAsync(Uri site, CancellationToken cancellationToken)
    {
        var home = new Uri(site.GetLeftPart(UriPartial.Authority) + "/");
        var candidates = new List<Uri>();
        try
        {
            var response = await http.GetAsync(new FetchRequest(site, "text/html"), cancellationToken);
            if (response.IsSuccess && response.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            {
                var document = Parser.ParseDocument(System.Text.Encoding.UTF8.GetString(response.Body));
                candidates.AddRange(document.QuerySelectorAll("link[rel][href]")
                    .Where(l => l.GetAttribute("rel")!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(r => r.Equals("icon", StringComparison.OrdinalIgnoreCase) || r.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase)))
                    .Select(l => UrlResolver.Resolve(l.GetAttribute("href"), response.FinalUri))
                    .OfType<string>()
                    .Select(u => new Uri(u))
                    .Take(3));
            }
        }
        catch (FetchException)
        {
            // Fall back to /favicon.ico.
        }

        candidates.Add(new Uri(home, "favicon.ico"));
        return candidates.DistinctBy(u => u.AbsoluteUri).ToList();
    }

    private async Task<long?> TryStoreAsync(Uri url, CancellationToken cancellationToken)
    {
        FetchResponse response;
        try
        {
            response = await http.GetAsync(new FetchRequest(url, "image/*"), cancellationToken);
        }
        catch (FetchException)
        {
            return null;
        }

        if (!response.IsSuccess || response.Body.Length == 0 || response.Body.Length > MaxBytes || ImageBytes.Sniff(response.Body) is not { } mime)
        {
            return null;
        }

        var hash = Domain.ArticleIdentity.Hash(Convert.ToBase64String(response.Body));
        var existing = await db.Icons.Where(i => i.Hash == hash).Select(i => (long?)i.Id).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var fileName = $"{hash}.{ImageBytes.Extension(mime)}";
        Directory.CreateDirectory(storage.IconDirectory);
        await File.WriteAllBytesAsync(Path.Combine(storage.IconDirectory, fileName), response.Body, cancellationToken);

        var icon = new Icon { Hash = hash, MimeType = mime, FileName = fileName, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.Icons.Add(icon);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return icon.Id;
        }
        catch (DbUpdateException)
        {
            // Another feed stored the same icon a moment ago.
            db.Entry(icon).State = EntityState.Detached;
            return await db.Icons.Where(i => i.Hash == hash).Select(i => (long?)i.Id).SingleOrDefaultAsync(cancellationToken);
        }
    }
}
