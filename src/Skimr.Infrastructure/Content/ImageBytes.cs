using System.Text;

namespace Skimr.Infrastructure.Content;

/// <summary>
/// Identifies image formats by their first bytes. Upstream Content-Type headers can't be trusted:
/// plenty of servers answer a missing favicon.ico with "200 OK" and an HTML page.
/// </summary>
internal static class ImageBytes
{
    public const string Svg = "image/svg+xml";

    /// <summary>The real MIME type, or null if the bytes aren't a supported image.</summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.Length >= 12 && data[4..8].SequenceEqual("ftyp"u8) && (data[8..12].SequenceEqual("avif"u8) || data[8..12].SequenceEqual("avis"u8)))
        {
            return "image/avif";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
        {
            return "image/x-icon";
        }

        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        // SVG is text: look for an <svg element near the start (after an XML prolog or comments).
        var head = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? Svg : null;
    }

    public static string Extension(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/avif" => "avif",
        "image/x-icon" => "ico",
        "image/bmp" => "bmp",
        Svg => "svg",
        _ => "bin",
    };

    /// <summary>
    /// SVG can carry script. Inside &lt;img&gt; it never runs, but someone could open the URL
    /// directly, so SVG is always served with a sandboxing CSP.
    /// </summary>
    public const string SvgSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
}
