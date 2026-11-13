using System.Xml.Linq;

namespace Skimr.Infrastructure.Feeds;

/// <summary>RSS 1.0 (and 0.90): items are siblings of the channel, not children.</summary>
internal static class RdfReader
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace[] RssNamespaces = ["http://purl.org/rss/1.0/", "http://my.netscape.com/rdf/simple/0.9/"];

    public static ParsedFeed Read(XElement root, Uri feedUri)
    {
        var ns = RssNamespaces.FirstOrDefault(n => root.Element(n + "channel") is not null)
            ?? throw new FeedParseException("RDF document has no RSS 1.0 channel.");
        var channel = root.Element(ns + "channel")!;

        var siteUrl = UrlResolver.Resolve(channel.Element(ns + "link")?.Value, feedUri);
        var itemBase = siteUrl is null ? feedUri : new Uri(siteUrl);

        var items = root.Elements(ns + "item").Select(item => new ParsedItem(
            Id: FeedParser.NullIfEmpty((string?)item.Attribute(Rdf + "about")),
            Url: UrlResolver.Resolve(item.Element(ns + "link")?.Value, itemBase),
            Title: HtmlText.ToPlainText(item.Element(ns + "title")?.Value),
            Author: FeedParser.NullIfEmpty(HtmlText.ToPlainText(item.Element(Dc + "creator")?.Value)),
            ContentHtml: FeedParser.NullIfEmpty(item.Element(Content + "encoded")?.Value ?? item.Element(ns + "description")?.Value),
            Published: FeedParser.ParseDate(item.Element(Dc + "date")?.Value),
            Updated: null,
            ThumbnailUrl: null)).ToList();

        return new ParsedFeed(
            Title: FeedParser.NullIfEmpty(HtmlText.ToPlainText(channel.Element(ns + "title")?.Value)),
            SiteUrl: siteUrl,
            Description: FeedParser.NullIfEmpty(HtmlText.ToPlainText(channel.Element(ns + "description")?.Value)),
            HubUrl: null,
            SelfUrl: null,
            Items: items);
    }
}
