using AngleSharp.Html.Parser;
using Skimr.Infrastructure.Fetching;

namespace Skimr.Infrastructure.Feeds;

internal sealed record DiscoveredFeed(string Url, string? Title);

/// <summary>
/// Turns what a user pasted into feed URLs. A feed URL is returned as-is; a web page is searched
/// for &lt;link rel="alternate"&gt; feeds, and failing that the usual paths are probed.
/// </summary>
internal sealed class FeedDiscovery(IOutboundHttp http)
{
    private const int MaxCandidates = 5;

    private static readonly string[] FeedTypes =
    [
        "application/rss+xml", "application/atom+xml", "application/feed+json",
        "application/json", "application/rdf+xml", "application/xml", "text/xml",
    ];

    private static readonly string[] CommonPaths = ["/feed", "/rss", "/atom.xml", "/index.xml", "/feed.xml", "/rss.xml", "/feed.json"];

    private static readonly HtmlParser Parser = new();

    public async Task<IReadOnlyList<DiscoveredFeed>> DiscoverAsync(string input, CancellationToken cancellationToken)
    {
        var uri = Normalise(input) ?? throw new FetchException($"'{input}' is not a web address.");

        var response = await http.GetAsync(new FetchRequest(uri, FeedRefresher.Accept), cancellationToken);
        if (!response.IsSuccess)
        {
            throw new FetchException($"{uri.Host} returned HTTP {(int)response.Status}.");
        }

        if (TryParse(response) is { } direct)
        {
            return [new DiscoveredFeed(response.FinalUri.AbsoluteUri, direct.Title)];
        }

        var candidates = Parser.ParseDocument(System.Text.Encoding.UTF8.GetString(response.Body))
            .QuerySelectorAll("link[rel][href]")
            .Where(l => l.GetAttribute("rel")!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase)
                && FeedTypes.Contains(l.GetAttribute("type")?.Split(';')[0].Trim().ToLowerInvariant()))
            .Select(l => UrlResolver.Resolve(l.GetAttribute("href"), response.FinalUri))
            .OfType<string>()
            .Distinct()
            .Take(MaxCandidates)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = CommonPaths.Select(p => new Uri(response.FinalUri, p).AbsoluteUri).ToList();
        }

        var found = new List<DiscoveredFeed>();
        foreach (var candidate in candidates)
        {
            if (await TryFetchFeedAsync(new Uri(candidate), cancellationToken) is { } feed)
            {
                found.Add(feed);
            }
        }

        return found.DistinctBy(f => f.Url).ToList();
    }

    private async Task<DiscoveredFeed?> TryFetchFeedAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetAsync(new FetchRequest(uri, FeedRefresher.Accept), cancellationToken);
            return response.IsSuccess && TryParse(response) is { } parsed
                ? new DiscoveredFeed(response.FinalUri.AbsoluteUri, parsed.Title)
                : null;
        }
        catch (FetchException)
        {
            return null;
        }
    }

    private static ParsedFeed? TryParse(FetchResponse response)
    {
        try
        {
            return FeedParser.Parse(response.Body, response.FinalUri);
        }
        catch (FeedParseException)
        {
            return null;
        }
    }

    /// <summary>Accepts "example.com" as well as full URLs.</summary>
    internal static Uri? Normalise(string input)
    {
        input = input.Trim();
        if (input.StartsWith("feed://", StringComparison.OrdinalIgnoreCase))
        {
            input = "https://" + input["feed://".Length..];
        }

        if (!input.Contains("://", StringComparison.Ordinal))
        {
            input = "https://" + input;
        }

        return Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri
            : null;
    }
}
