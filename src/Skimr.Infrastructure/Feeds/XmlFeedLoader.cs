using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Skimr.Infrastructure.Feeds;

/// <summary>
/// Loads feed XML safely and tolerantly. DTDs are ignored and never resolved (PLAN D25),
/// so external entities can't read files and entity bombs can't expand. Broken documents
/// get one repair pass: encoding fallback, stray bytes before the prolog, invalid control
/// characters, bare ampersands and HTML-only entities.
/// </summary>
internal static partial class XmlFeedLoader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
        // XNodeReader reports kept whitespace as Text, which the Syndication formatters reject.
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 50_000_000,
    };

    static XmlFeedLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static XDocument Load(byte[] body)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(body), Settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // Fall through to the repair pass.
        }

        var repaired = Repair(Decode(body));
        try
        {
            using var reader = XmlReader.Create(new StringReader(repaired), Settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FeedParseException($"Not valid XML even after repair: {ex.Message}", ex);
        }
    }

    internal static string Decode(byte[] body)
    {
        // A BOM wins. Otherwise the declared encoding, unless it's UTF-8 and the bytes aren't.
        if (body.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(body, 3, body.Length - 3);
        }

        if (body.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || body.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            using var sr = new StreamReader(new MemoryStream(body), detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }

        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 200));
        var declared = DeclaredEncoding().Match(head);
        Encoding encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
        if (declared.Success)
        {
            try
            {
                encoding = Encoding.GetEncoding(declared.Groups[1].Value);
            }
            catch (ArgumentException)
            {
                // Unknown name: keep UTF-8.
            }
        }

        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
            }
            catch (DecoderFallbackException)
            {
                // Labelled UTF-8 but isn't: the usual culprit is Windows-1252.
                return Encoding.GetEncoding(1252).GetString(body);
            }
        }

        return encoding.GetString(body);
    }

    internal static string Repair(string xml)
    {
        xml = xml.TrimStart('﻿', ' ', '\t', '\r', '\n');
        xml = InvalidXmlChars().Replace(xml, "");
        // The text is already decoded, so the declared encoding no longer applies.
        xml = EncodingAttribute().Replace(xml, "", 1);
        return Entity().Replace(xml, m =>
        {
            var name = m.Groups["name"];
            if (!name.Success)
            {
                return "&amp;";
            }

            if (name.Value is "amp" or "lt" or "gt" or "quot" or "apos" || name.Value.StartsWith('#'))
            {
                return m.Value;
            }

            // HTML named entity (&nbsp; &mdash; ...) becomes a numeric reference XML understands.
            var decoded = WebUtility.HtmlDecode(m.Value);
            return decoded != m.Value
                ? string.Concat(decoded.EnumerateRunes().Select(r => $"&#{r.Value};"))
                : "&amp;" + m.Value[1..];
        });
    }

    [GeneratedRegex("""encoding\s*=\s*["']([A-Za-z0-9._-]+)["']""")]
    private static partial Regex DeclaredEncoding();

    [GeneratedRegex("""(?<=<\?xml[^>]*?)\s+encoding\s*=\s*["'][^"']*["']""")]
    private static partial Regex EncodingAttribute();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F￾￿]")]
    private static partial Regex InvalidXmlChars();

    [GeneratedRegex(@"&(?:(?<name>#[0-9]+|#x[0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]*);)?")]
    private static partial Regex Entity();
}
