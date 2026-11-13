using System.ServiceModel.Syndication;
using System.Xml;
using System.Xml.Linq;

namespace Skimr.Infrastructure.Feeds;

/// <summary>
/// Parses RSS 0.9x/2.0 and Atom 1.0 with System.ServiceModel.Syndication, and RSS 1.0/RDF
/// and JSON Feed with small custom readers. Text comes out plain; HTML comes out unsanitised.
/// </summary>
internal static class FeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    /// <param name="body">Raw response bytes.</param>
    /// <param name="feedUri">Where the document came from; the last resort for resolving relative URLs.</param>
    public static ParsedFeed Parse(byte[] body, Uri feedUri)
    {
        if (LooksLikeJson(body))
        {
            return JsonFeedReader.Read(body, feedUri);
        }

        var document = XmlFeedLoader.Load(body);
        var root = document.Root ?? throw new FeedParseException("Empty XML document.");

        return root.Name.LocalName switch
        {
            "rss" => ReadSyndication(PrepareRss(document), new Rss20FeedFormatter(), feedUri),
            "feed" when root.Name.Namespace == Atom => ReadSyndication(document, new Atom10FeedFormatter(), feedUri),
            "RDF" => RdfReader.Read(root, feedUri),
            _ => throw new FeedParseException($"Not a feed: root element is <{root.Name.LocalName}>."),
        };
    }

    private static bool LooksLikeJson(byte[] body)
    {
        foreach (var b in body.AsSpan(body.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0))
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b == (byte)'{';
        }

        return false;
    }

    /// <summary>The RSS 2.0 formatter refuses version="0.91"/"0.92"; their elements are a subset, so relabel.</summary>
    private static XDocument PrepareRss(XDocument document)
    {
        document.Root!.SetAttributeValue("version", "2.0");
        return document;
    }

    private static ParsedFeed ReadSyndication(XDocument document, SyndicationFeedFormatter formatter, Uri feedUri)
    {
        formatter.DateTimeParser = static (XmlDateTimeData data, out DateTimeOffset result) =>
        {
            // Never fail the whole feed over one date: unknown dates become MinValue and are treated as missing.
            result = DateParser.TryParse(data.DateTimeString, out var utc) ? new DateTimeOffset(utc) : DateTimeOffset.MinValue;
            return true;
        };
        formatter.UriParser = static (XmlUriData data, out Uri? uri) =>
        {
            if (!Uri.TryCreate(data.UriString?.Trim(), UriKind.RelativeOrAbsolute, out uri))
            {
                uri = new Uri("about:invalid");
            }

            return true;
        };

        SyndicationFeed feed;
        try
        {
            using var reader = document.CreateReader();
            reader.MoveToContent();
            formatter.ReadFrom(reader);
            feed = formatter.Feed;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or NotSupportedException or ArgumentException)
        {
            throw new FeedParseException($"Could not read feed: {ex.Message}", ex);
        }

        var feedBase = Combine(feedUri, feed.BaseUri);
        var siteUrl = UrlResolver.Resolve(Link(feed.Links, "alternate")?.OriginalString, feedBase);
        var itemFallbackBase = siteUrl is null ? feedBase : new Uri(siteUrl);

        var isRss = formatter is Rss20FeedFormatter;
        var items = feed.Items.Select(item => MapItem(item, Combine(itemFallbackBase, item.BaseUri), isRss)).ToList();

        return new ParsedFeed(
            Title: NullIfEmpty(HtmlText.ToPlainText(feed.Title?.Text)),
            SiteUrl: siteUrl,
            Description: NullIfEmpty(HtmlText.ToPlainText(feed.Description?.Text)),
            HubUrl: UrlResolver.Resolve(Link(feed.Links, "hub")?.OriginalString, feedBase),
            SelfUrl: UrlResolver.Resolve(Link(feed.Links, "self")?.OriginalString, feedBase),
            Items: items);
    }

    private static ParsedItem MapItem(SyndicationItem item, Uri baseUri, bool isRss)
    {
        var extensions = item.ElementExtensions
            .Select(e => e.GetObject<XElement>())
            .ToList();

        var id = NullIfEmpty(item.Id?.Trim());

        // The RSS formatter turns a permalink <guid> into an alternate link even when it isn't a URL.
        var link = item.Links
            .Where(l => string.IsNullOrEmpty(l.RelationshipType) || l.RelationshipType == "alternate")
            .Select(l => l.Uri?.OriginalString)
            .Where(href => href is not null && (href != id || Uri.IsWellFormedUriString(href, UriKind.Absolute)))
            .Select(href => UrlResolver.Resolve(href, baseUri))
            .FirstOrDefault(url => url is not null);

        var html = Ext(extensions, Content + "encoded")?.Value
            ?? ContentHtml(item.Content)
            // RSS <description> is HTML by convention, though the formatter labels it "text".
            ?? (isRss ? NullIfEmpty(item.Summary?.Text) : ContentHtml(item.Summary));

        var published = Valid(item.PublishDate) ?? Valid(item.LastUpdatedTime) ?? ParseDate(Ext(extensions, Dc + "date")?.Value);

        var author = item.Authors.Select(a => a.Name ?? a.Email).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            ?? Ext(extensions, Dc + "creator")?.Value;

        return new ParsedItem(
            Id: id,
            Url: link,
            Title: HtmlText.ToPlainText(item.Title?.Text),
            Author: NullIfEmpty(HtmlText.ToPlainText(author)),
            ContentHtml: html,
            Published: published,
            Updated: Valid(item.LastUpdatedTime),
            ThumbnailUrl: UrlResolver.Resolve(Thumbnail(extensions, item.Links), baseUri));
    }

    private static string? Thumbnail(List<XElement> extensions, IEnumerable<SyndicationLink> links)
    {
        var media = extensions.SelectMany(e => e.DescendantsAndSelf()).Where(e => e.Name.Namespace == Media).ToList();

        return media.FirstOrDefault(e => e.Name.LocalName == "thumbnail")?.Attribute("url")?.Value
            ?? media.FirstOrDefault(e => e.Name.LocalName == "content"
                && ((string?)e.Attribute("medium") == "image" || ((string?)e.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
                ?.Attribute("url")?.Value
            ?? links.FirstOrDefault(l => l.RelationshipType == "enclosure"
                && l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)?.Uri?.OriginalString;
    }

    private static string? ContentHtml(SyndicationContent? content) => content switch
    {
        TextSyndicationContent { Type: "text" } text => NullIfEmpty(HtmlText.FromPlainText(text.Text)),
        TextSyndicationContent text => NullIfEmpty(text.Text),
        XmlSyndicationContent xml => NullIfEmpty(ReadXhtml(xml)),
        _ => null,
    };

    private static string ReadXhtml(XmlSyndicationContent xml)
    {
        using var reader = xml.GetReaderAtContent();
        return reader.ReadInnerXml();
    }

    private static XElement? Ext(List<XElement> extensions, XName name) =>
        extensions.FirstOrDefault(e => e.Name == name);

    private static Uri? Link(IEnumerable<SyndicationLink> links, string rel) =>
        links.FirstOrDefault(l => l.RelationshipType == rel)?.Uri;

    private static Uri Combine(Uri fallback, Uri? baseUri) =>
        baseUri is null ? fallback
        : baseUri.IsAbsoluteUri ? baseUri
        : Uri.TryCreate(fallback, baseUri, out var combined) ? combined : fallback;

    private static DateTime? Valid(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue ? null : value.UtcDateTime;

    internal static DateTime? ParseDate(string? text) =>
        DateParser.TryParse(text, out var utc) ? utc : null;

    internal static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
