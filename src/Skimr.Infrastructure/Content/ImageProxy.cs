using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace Skimr.Infrastructure.Content;

/// <summary>
/// Rewrites article images to load through Skimr (PLAN D54): the reader's IP and referrer never
/// reach image hosts, http:// images work, and the CSP keeps img-src to 'self'. URLs are
/// HMAC-signed so the endpoint isn't an open proxy for arbitrary URLs. A user can still get a
/// URL of their choice signed (by publishing a feed containing it), so the endpoint's real
/// protections are the SSRF guard, the image checks, the size cap, sign-in and a rate limit.
/// Rewriting happens when rendering, not at ingest, so switching the proxy off takes effect at once.
/// </summary>
public sealed class ImageProxy(ContentOptions options, ImageProxyKey key)
{
    public const string RoutePrefix = "img/";

    private static readonly HtmlParser Parser = new();

    public bool Enabled => options.ImageProxyEnabled;

    /// <summary>The proxied URL (relative to the app's base) for an absolute http(s) image URL; anything else is returned unchanged.</summary>
    public string? Url(string? original)
    {
        if (!Enabled || original is null || !Uri.TryCreate(original, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return original;
        }

        var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        return $"{RoutePrefix}{Sign(uri.AbsoluteUri)}/{payload}";
    }

    /// <summary>Rewrites every &lt;img src&gt; in already-sanitised HTML.</summary>
    public string RewriteHtml(string html)
    {
        if (!Enabled || string.IsNullOrEmpty(html) || !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var document = Parser.ParseDocument("<body>" + html);
        foreach (var img in document.QuerySelectorAll("img[src]"))
        {
            img.SetAttribute("src", Url(img.GetAttribute("src")));
        }

        return document.Body!.InnerHtml;
    }

    /// <summary>The original URL if the signature is valid, otherwise null.</summary>
    public string? Verify(string signature, string payload)
    {
        try
        {
            var original = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payload));
            var expected = Encoding.ASCII.GetBytes(Sign(original));
            return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature)) ? original : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string Sign(string url) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key.Bytes, Encoding.UTF8.GetBytes(url)).AsSpan(0, 16));
}

/// <summary>The image proxy's HMAC key, kept in the data directory so URLs survive restarts.</summary>
public sealed class ImageProxyKey(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;

    public static ImageProxyKey LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "keys", "image-proxy.key");
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
            {
                return new ImageProxyKey(existing);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var created = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, created);
        return new ImageProxyKey(created);
    }
}
