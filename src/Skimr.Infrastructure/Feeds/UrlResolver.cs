namespace Skimr.Infrastructure.Feeds;

internal static class UrlResolver
{
    /// <summary>Resolves against <paramref name="baseUri"/> and keeps only absolute http(s) URLs.</summary>
    public static string? Resolve(string? url, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();

        // On Linux "/path" parses as an absolute file:// URI, so treat a leading slash as relative.
        Uri? uri = null;
        var isAbsolute = !url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out uri);
        // "ht!tp://x" is technically a relative path, but it's a broken absolute URL, not a real one.
        if (!isAbsolute && (baseUri is null || url.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(baseUri, url, out uri)))
        {
            return null;
        }

        return uri!.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
