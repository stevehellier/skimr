using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record FullTextResult(string? Html, string? Error);

/// <summary>
/// "Load full article": fetches the original page through the SSRF-guarded client, extracts the
/// main text with SmartReader, sanitises it and caches it on the article for every reader.
/// SmartReader is given the HTML as text, so it never makes requests of its own (it would with
/// only a URL, bypassing the guard).
/// </summary>
internal sealed partial class FullTextService(IDbContextFactory<SkimrDbContext> contexts, IOutboundHttp http, ContentSanitizer sanitizer, TimeProvider time)
{
    static FullTextService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<FullTextResult> GetAsync(Guid userId, long articleId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // Only for articles in the user's own lists.
        var article = await db.Articles.SingleOrDefaultAsync(
            a => a.Id == articleId && db.UserArticles.Any(u => u.UserId == userId && u.ArticleId == articleId), cancellationToken);
        if (article is null)
        {
            return new FullTextResult(null, "Article not found.");
        }

        if (article.FullTextHtml is { } cached)
        {
            return new FullTextResult(cached, null);
        }

        if (article.Url is null)
        {
            return new FullTextResult(null, "This article has no link to load.");
        }

        FetchResponse response;
        try
        {
            response = await http.GetAsync(new FetchRequest(new Uri(article.Url), "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5"), cancellationToken);
        }
        catch (FetchException ex)
        {
            return new FullTextResult(null, ex.Message);
        }

        if (!response.IsSuccess)
        {
            return new FullTextResult(null, $"The site answered with HTTP {(int)response.Status}.");
        }

        if (response.ContentType is { } type && !type.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return new FullTextResult(null, $"That link isn't a web page ({type}).");
        }

        var finalUrl = response.FinalUri.AbsoluteUri;
        SmartReader.Article extracted;
        try
        {
            extracted = new SmartReader.Reader(finalUrl, Decode(response.Body, response.Charset)).GetArticle();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Untrusted HTML; whatever the extractor throws, report it instead of failing the page.
            return new FullTextResult(null, "Couldn't read the article text on that page.");
        }

        if (!extracted.IsReadable || string.IsNullOrWhiteSpace(extracted.Content))
        {
            return new FullTextResult(null, "Couldn't find the article text on that page.");
        }

        var clean = sanitizer.Sanitize(extracted.Content, finalUrl);
        article.FullTextHtml = StoredText.Fit(clean.Html, int.MaxValue);
        article.FullTextFetchedAt = time.GetUtcNow().UtcDateTime;
        // Search the whole article from now on, not just the feed's excerpt.
        article.SearchText = StoredText.Fit(HtmlText.ToPlainText(clean.Html), ArticleIngester.SearchTextLimit);
        await db.SaveChangesAsync(cancellationToken);
        return new FullTextResult(article.FullTextHtml, null);
    }

    /// <summary>Charset from the Content-Type header, else &lt;meta charset&gt;, else UTF-8.</summary>
    internal static string Decode(byte[] body, string? headerCharset)
    {
        var name = headerCharset;
        if (string.IsNullOrWhiteSpace(name))
        {
            var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 4096));
            var meta = MetaCharset().Match(head);
            name = meta.Success ? meta.Groups[1].Value : null;
        }

        try
        {
            return (name is null ? Encoding.UTF8 : Encoding.GetEncoding(name)).GetString(body);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(body);
        }
    }

    [GeneratedRegex("""<meta[^>]+charset\s*=\s*["']?([A-Za-z0-9._-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
