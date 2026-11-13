namespace Skimr.Infrastructure.Feeds;

/// <summary>Format-neutral parse result. Text fields are plain text; HTML fields are unsanitised.</summary>
internal sealed record ParsedFeed(
    string? Title,
    string? SiteUrl,
    string? Description,
    string? HubUrl,
    string? SelfUrl,
    IReadOnlyList<ParsedItem> Items);

internal sealed record ParsedItem(
    string? Id,
    string? Url,
    string Title,
    string? Author,
    string? ContentHtml,
    DateTime? Published,
    DateTime? Updated,
    string? ThumbnailUrl);

internal sealed class FeedParseException(string message, Exception? inner = null) : Exception(message, inner);
