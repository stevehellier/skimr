namespace Skimr.Web.Auth;

internal static class SafeRedirect
{
    /// <summary>
    /// Returns <paramref name="url"/> only if it's a path on this site; otherwise the home page.
    /// Rejects "//evil", "/\evil" and anything absolute, which browsers would treat as another host.
    /// </summary>
    public static string Local(string? url, string fallback = "/")
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return fallback;
        }

        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return fallback;
        }

        return url.Any(char.IsControl) ? fallback : url;
    }
}
