using System.Text.Json;

namespace Skimr.Infrastructure.Feeds;

/// <summary>JSON Feed 1.0 and 1.1. Tolerates numeric ids and missing optional fields.</summary>
internal static class JsonFeedReader
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    public static ParsedFeed Read(byte[] body, Uri feedUri)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, Options);
        }
        catch (JsonException ex)
        {
            throw new FeedParseException($"Not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !(Str(root, "version")?.StartsWith("https://jsonfeed.org/version/", StringComparison.Ordinal) ?? false))
            {
                throw new FeedParseException("JSON document is not a JSON Feed.");
            }

            var siteUrl = UrlResolver.Resolve(Str(root, "home_page_url"), feedUri);
            var itemBase = siteUrl is null ? feedUri : new Uri(siteUrl);

            var items = root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => ReadItem(i, itemBase)).ToList()
                : [];

            var hub = root.TryGetProperty("hubs", out var hubs) && hubs.ValueKind == JsonValueKind.Array
                ? hubs.EnumerateArray()
                    .Where(h => h.ValueKind == JsonValueKind.Object && string.Equals(Str(h, "type"), "WebSub", StringComparison.OrdinalIgnoreCase))
                    .Select(h => Str(h, "url"))
                    .FirstOrDefault()
                : null;

            return new ParsedFeed(
                Title: FeedParser.NullIfEmpty(HtmlText.ToPlainText(Str(root, "title"))),
                SiteUrl: siteUrl,
                Description: FeedParser.NullIfEmpty(HtmlText.ToPlainText(Str(root, "description"))),
                HubUrl: UrlResolver.Resolve(hub, feedUri),
                SelfUrl: UrlResolver.Resolve(Str(root, "feed_url"), feedUri),
                Items: items);
        }
    }

    private static ParsedItem ReadItem(JsonElement item, Uri baseUri)
    {
        var html = FeedParser.NullIfEmpty(Str(item, "content_html"))
            ?? (Str(item, "content_text") is { } text ? FeedParser.NullIfEmpty(HtmlText.FromPlainText(text)) : null)
            ?? FeedParser.NullIfEmpty(Str(item, "summary"));

        var author = item.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
            ? authors.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a => Str(a, "name")).FirstOrDefault(n => n is not null)
            : item.TryGetProperty("author", out var single) && single.ValueKind == JsonValueKind.Object ? Str(single, "name") : null;

        return new ParsedItem(
            Id: FeedParser.NullIfEmpty(Str(item, "id")),
            Url: UrlResolver.Resolve(Str(item, "url") ?? Str(item, "external_url"), baseUri),
            Title: HtmlText.ToPlainText(Str(item, "title")),
            Author: FeedParser.NullIfEmpty(author),
            ContentHtml: html,
            Published: FeedParser.ParseDate(Str(item, "date_published")) ?? FeedParser.ParseDate(Str(item, "date_modified")),
            Updated: FeedParser.ParseDate(Str(item, "date_modified")),
            ThumbnailUrl: UrlResolver.Resolve(Str(item, "image") ?? Str(item, "banner_image"), baseUri));
    }

    /// <summary>String or number as text (JSON Feed 1.0 publishers sometimes use numeric ids).</summary>
    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
