using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record OpmlImportResult(int Added, int AlreadySubscribed, int FoldersCreated, IReadOnlyList<string> Problems);

/// <summary>
/// OPML in and out. Skimr's folders are flat like Feedly's, so nested OPML folders are
/// flattened into names like "Tech / Rust" (agreed 2026-11-13, PLAN D53). Import doesn't fetch
/// anything while the user waits: new feeds are due at once and the scheduler picks them up.
/// </summary>
internal sealed class OpmlService(IDbContextFactory<SkimrDbContext> contexts, LibraryService library, TimeProvider time)
{
    public const long MaxUploadBytes = 2 * 1024 * 1024;
    private const int MaxFeeds = 5000;
    private const string FolderSeparator = " / ";

    public async Task<string> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var sources = await db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.SortOrder)
            .Select(s => new SourceRow(s.CustomTitle ?? s.Feed!.Title ?? s.Feed.Url, s.Feed!.Url, s.Feed.SiteUrl, s.Folders.Select(f => f.FolderId).ToList()))
            .ToListAsync(cancellationToken);
        var folders = await db.Folders.Where(f => f.UserId == userId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(cancellationToken);

        static XElement Outline(SourceRow s) => new("outline",
            new XAttribute("type", "rss"),
            new XAttribute("text", s.Title),
            new XAttribute("title", s.Title),
            new XAttribute("xmlUrl", s.Url),
            s.SiteUrl is null ? null : new XAttribute("htmlUrl", s.SiteUrl));

        var body = new XElement("body",
            folders.Select(f => new XElement("outline",
                new XAttribute("text", f.Name),
                new XAttribute("title", f.Name),
                // A source in several folders appears under each of them.
                sources.Where(s => s.Folders.Contains(f.Id)).Select(s => Outline(s)))),
            sources.Where(s => s.Folders.Count == 0).Select(s => Outline(s)));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", "Skimr subscriptions"),
                    new XElement("dateCreated", time.GetUtcNow().ToString("R"))),
                body));
        return document.Declaration + Environment.NewLine + document.ToString();
    }

    public async Task<OpmlImportResult> ImportAsync(Guid userId, byte[] opml, CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            // Same safe, tolerant loader as feeds: no DTDs, no external entities (PLAN D25).
            document = XmlFeedLoader.Load(opml);
        }
        catch (FeedParseException ex)
        {
            return new OpmlImportResult(0, 0, 0, [$"That file isn't valid OPML: {ex.Message}"]);
        }

        var body = document.Root?.Name.LocalName.Equals("opml", StringComparison.OrdinalIgnoreCase) == true
            ? document.Root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase))
            : null;
        if (body is null)
        {
            return new OpmlImportResult(0, 0, 0, ["That file isn't OPML (no <opml><body>)."]);
        }

        var entries = new List<(string Url, string? Title, string? Folder)>();
        Collect(body, null, entries);

        var existingFolders = (await library.ListFoldersAsync(userId, cancellationToken)).ToDictionary(f => f.Name, f => f.Id, StringComparer.OrdinalIgnoreCase);
        int added = 0, already = 0, foldersCreated = 0;
        var problems = new List<string>();

        foreach (var entry in entries.Take(MaxFeeds))
        {
            long? folderId = null;
            if (entry.Folder is { } folderName)
            {
                if (!existingFolders.TryGetValue(folderName, out var id))
                {
                    var created = await library.CreateFolderAsync(userId, folderName, cancellationToken);
                    if (!created.Succeeded)
                    {
                        problems.Add($"Folder “{folderName}”: {created.Error}");
                        continue;
                    }

                    id = created.Id!.Value;
                    existingFolders[folderName] = id;
                    foldersCreated++;
                }

                folderId = id;
            }

            var before = await IsSubscribedAsync(userId, entry.Url, cancellationToken);
            var result = await library.SubscribeAsync(userId, entry.Url, folderId is { } f ? [f] : [], cancellationToken, entry.Title);
            if (!result.Succeeded)
            {
                problems.Add($"{entry.Url}: {result.Error}");
                continue;
            }

            if (before)
            {
                already++;
                if (folderId is { } fid)
                {
                    await library.AddToFolderAsync(userId, result.Id!.Value, fid, cancellationToken);
                }
            }
            else
            {
                added++;
            }
        }

        if (entries.Count > MaxFeeds)
        {
            problems.Add($"Only the first {MaxFeeds} feeds were imported.");
        }

        return new OpmlImportResult(added, already, foldersCreated, problems);
    }

    private async Task<bool> IsSubscribedAsync(Guid userId, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var normalised = uri.AbsoluteUri;
        return await db.Subscriptions.AnyAsync(s => s.UserId == userId && s.Feed!.Url == normalised, cancellationToken);
    }

    /// <summary>Feeds are outlines with an xmlUrl (any letter case, with or without type="rss"); other outlines are folders.</summary>
    private static void Collect(XElement parent, string? folder, List<(string Url, string? Title, string? Folder)> entries)
    {
        foreach (var outline in parent.Elements().Where(e => e.Name.LocalName.Equals("outline", StringComparison.OrdinalIgnoreCase)))
        {
            var title = Attr(outline, "title") ?? Attr(outline, "text");
            if (Attr(outline, "xmlUrl") is { } url)
            {
                entries.Add((url.Trim(), title, folder));
            }
            else if (outline.HasElements)
            {
                var name = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
                Collect(outline, folder is null ? name : folder + FolderSeparator + name, entries);
            }
        }
    }

    private sealed record SourceRow(string Title, string Url, string? SiteUrl, List<long> Folders);

    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value
            ? value
            : null;
}
