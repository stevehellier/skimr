using Skimr.Infrastructure.Content;

namespace Skimr.UnitTests.Content;

public sealed class ContentSanitizerTests
{
    private readonly ContentSanitizer _sanitizer = new(new ContentOptions());

    private string Clean(string html, string? baseUrl = null) => _sanitizer.Sanitize(html, baseUrl).Html;

    [Theory]
    [InlineData("<script>alert(1)</script><p>ok</p>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"jav&#x09;ascript:alert(1)\">x</a>")]
    [InlineData("<a href=\" JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<svg onload=alert(1)>")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>")]
    [InlineData("<object data=\"https://evil.example/x.swf\"></object><embed src=\"https://evil.example/x\">")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [InlineData("<base href=\"https://evil.example/\">")]
    [InlineData("<form action=\"https://evil.example\"><input name=password><button>Go</button></form>")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">x</div>")]
    [InlineData("<p onclick=\"alert(1)\" onmouseover=\"alert(1)\">x</p>")]
    public void Dangerous_markup_is_neutralised(string html)
    {
        var clean = Clean(html).ToLowerInvariant();

        clean.Should().NotContain("<script")
            .And.NotContain("javascript:")
            .And.NotContain("onerror")
            .And.NotContain("onload")
            .And.NotContain("onclick")
            .And.NotContain("onmouseover")
            .And.NotContain("<object")
            .And.NotContain("<embed")
            .And.NotContain("<meta")
            .And.NotContain("<base")
            .And.NotContain("<form")
            .And.NotContain("<input")
            .And.NotContain("data:text/html")
            .And.NotContain("style=");
    }

    [Fact]
    public void Layout_and_dom_hooks_are_stripped_but_content_kept()
    {
        var clean = Clean("<p class=\"x\" id=\"main\" style=\"position:fixed\" name=\"location\">Hello <b>world</b></p><style>body{display:none}</style>");

        clean.Should().Be("<p>Hello <b>world</b></p>");
    }

    [Fact]
    public void Relative_urls_resolve_against_the_article()
    {
        var clean = Clean("<a href=\"../about\">a</a><img src=\"img/p.png\">", "https://site.example/blog/post/");

        clean.Should().Contain("href=\"https://site.example/blog/about\"")
            .And.Contain("src=\"https://site.example/blog/post/img/p.png\"");
    }

    [Fact]
    public void Links_open_safely_in_a_new_tab_and_images_load_lazily()
    {
        var clean = Clean("<a href=\"https://x.example/\">x</a><img src=\"https://x.example/i.png\">");

        clean.Should().Contain("target=\"_blank\"")
            .And.Contain("rel=\"noopener noreferrer nofollow ugc\"")
            .And.Contain("loading=\"lazy\"")
            .And.Contain("referrerpolicy=\"no-referrer\"");
    }

    [Fact]
    public void Allowed_embeds_are_kept_and_sandboxed()
    {
        var clean = Clean("<iframe src=\"https://www.youtube-nocookie.com/embed/abc\" onload=\"x()\" allowfullscreen></iframe>");

        clean.Should().Contain("<iframe src=\"https://www.youtube-nocookie.com/embed/abc\"")
            .And.Contain("sandbox=\"allow-scripts allow-same-origin allow-presentation allow-popups\"")
            .And.NotContain("onload");
    }

    [Theory]
    [InlineData("<iframe src=\"https://evil.example/frame\"></iframe>", "Embedded content: evil.example")]
    [InlineData("<iframe src=\"http://www.youtube.com/embed/abc\"></iframe>", "Embedded content: www.youtube.com")]
    [InlineData("<iframe srcdoc=\"&lt;script&gt;alert(1)&lt;/script&gt;\"></iframe>", "")]
    public void Other_iframes_become_links_or_vanish(string html, string expectedText)
    {
        var clean = Clean(html);

        clean.Should().NotContain("<iframe").And.NotContain("srcdoc");
        if (expectedText.Length > 0)
        {
            clean.Should().Contain(expectedText);
        }
    }

    [Fact]
    public void Snippet_is_plain_text_and_first_image_is_found()
    {
        var result = _sanitizer.Sanitize("<p>Hello&nbsp;<b>there</b></p><img src=\"https://x.example/a.png\"><img src=\"https://x.example/b.png\">", null);

        result.Snippet.Should().Be("Hello there");
        result.FirstImageUrl.Should().Be("https://x.example/a.png");
    }

    [Fact]
    public void Long_snippets_are_truncated()
    {
        var result = _sanitizer.Sanitize("<p>" + new string('a', 1000) + "</p>", null);

        result.Snippet.Length.Should().Be(300);
        result.Snippet.Should().EndWith("…");
    }
}
