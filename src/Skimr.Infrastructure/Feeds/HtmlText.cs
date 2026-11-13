using System.Net;
using AngleSharp.Html.Parser;

namespace Skimr.Infrastructure.Feeds;

internal static class HtmlText
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Decodes entities and drops tags: "A &lt;b&gt;bold&lt;/b&gt; &amp;amp; more" becomes "A bold &amp; more".</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        if (html.IndexOfAny(['<', '&']) < 0)
        {
            return Collapse(html);
        }

        var document = Parser.ParseDocument("<body>" + html);
        return Collapse(document.Body?.TextContent ?? "");
    }

    /// <summary>Text content (e.g. JSON Feed content_text, Atom type="text") as safe HTML paragraphs.</summary>
    public static string FromPlainText(string text) =>
        string.Concat(text.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line => line.Trim().Length > 0)
            .Select(line => "<p>" + WebUtility.HtmlEncode(line.Trim()) + "</p>"));

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
