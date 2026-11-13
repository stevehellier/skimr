using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Skimr.Infrastructure.Feeds;

namespace Skimr.Infrastructure.Content;

internal sealed record SanitizedContent(string Html, string Snippet, string? FirstImageUrl);

/// <summary>
/// The single gate for untrusted HTML (feed content and extracted full text). Output is safe
/// to store and render. Configured once; <c>Sanitize</c> is thread-safe, so this is a singleton.
/// </summary>
internal sealed class ContentSanitizer
{
    private const int MaxInputChars = 1_000_000;
    private const int SnippetChars = 300;

    private static readonly HtmlParser Parser = new();

    private readonly HtmlSanitizer _sanitizer;
    private readonly HashSet<string> _embedHosts;

    public ContentSanitizer(ContentOptions options)
    {
        _embedHosts = new HashSet<string>(options.AllowedEmbedHosts, StringComparer.OrdinalIgnoreCase);

        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedSchemes.Add("mailto");

        foreach (var tag in new[] { "form", "input", "button", "textarea", "select", "option", "optgroup", "fieldset", "legend", "label", "style", "link", "meta", "base" })
        {
            _sanitizer.AllowedTags.Remove(tag);
        }

        _sanitizer.AllowedTags.Add("iframe");

        // style breaks layout (and the CSP blocks it anyway); class/id/name could hook or clobber app CSS and DOM.
        foreach (var attribute in new[] { "style", "class", "id", "name", "tabindex", "accesskey" })
        {
            _sanitizer.AllowedAttributes.Remove(attribute);
        }

        _sanitizer.AllowedAttributes.Add("allowfullscreen");
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();

        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element)
            {
                return;
            }

            switch (element.LocalName)
            {
                case "a":
                    element.SetAttribute("target", "_blank");
                    element.SetAttribute("rel", "noopener noreferrer nofollow ugc");
                    break;
                case "img":
                    element.SetAttribute("loading", "lazy");
                    element.SetAttribute("decoding", "async");
                    element.SetAttribute("referrerpolicy", "no-referrer");
                    break;
                case "iframe":
                    ProcessIframe(element, e);
                    break;
            }
        };
    }

    public SanitizedContent Sanitize(string? html, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new SanitizedContent("", "", null);
        }

        if (html.Length > MaxInputChars)
        {
            html = html[..MaxInputChars];
        }

        var clean = _sanitizer.Sanitize(html, baseUrl ?? "");

        var document = Parser.ParseDocument("<body>" + clean);
        var snippet = HtmlText.Truncate(HtmlText.ToPlainText(document.Body?.InnerHtml), SnippetChars);
        var firstImage = document.QuerySelectorAll("img[src]")
            .Select(img => UrlResolver.Resolve(img.GetAttribute("src"), null))
            .FirstOrDefault(src => src is not null);

        return new SanitizedContent(clean, snippet, firstImage);
    }

    private void ProcessIframe(IElement iframe, PostProcessNodeEventArgs e)
    {
        var src = iframe.GetAttribute("src");
        if (Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.Scheme == "https" && _embedHosts.Contains(uri.Host))
        {
            iframe.SetAttribute("sandbox", "allow-scripts allow-same-origin allow-presentation allow-popups");
            iframe.SetAttribute("loading", "lazy");
            // YouTube refuses embeds without a referrer, and the page-wide policy is no-referrer.
            iframe.SetAttribute("referrerpolicy", "strict-origin-when-cross-origin");
            return;
        }

        // Not an allowed embed: swap it for a plain link (or nothing) so no frame ever loads.
        if (src is not null && uri is { Scheme: "http" or "https" })
        {
            var link = e.Document.CreateElement("a");
            link.SetAttribute("href", uri.AbsoluteUri);
            link.SetAttribute("target", "_blank");
            link.SetAttribute("rel", "noopener noreferrer nofollow ugc");
            link.TextContent = "Embedded content: " + uri.Host;
            e.ReplacementNodes.Add(link);
        }
        else
        {
            e.ReplacementNodes.Add(e.Document.CreateTextNode(""));
        }
    }
}
