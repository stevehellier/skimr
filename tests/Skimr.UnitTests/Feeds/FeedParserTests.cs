using Skimr.Infrastructure.Feeds;

namespace Skimr.UnitTests.Feeds;

public sealed class FeedParserTests
{
    private static ParsedFeed Parse(string fixture, string feedUrl = "https://feeds.example.com/feed") =>
        FeedParser.Parse(Fixtures.Feed(fixture), new Uri(feedUrl));

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Rss2_wordpress_reads_extensions_and_feed_links()
    {
        var feed = Parse("rss2-wordpress.xml");

        feed.Title.Should().Be("Example & Co Blog");
        feed.Description.Should().Be("Notes and things");
        feed.SiteUrl.Should().Be("https://blog.example.com/");
        feed.SelfUrl.Should().Be("https://blog.example.com/feed/");
        feed.HubUrl.Should().Be("https://pubsubhubbub.appspot.com/");
        feed.Items.Should().HaveCount(2);

        var first = feed.Items[0];
        first.Id.Should().Be("https://blog.example.com/?p=101");
        first.Url.Should().Be("https://blog.example.com/2026/11/first-post/");
        first.Title.Should().Be("First post & more");
        first.Author.Should().Be("Jane Doe");
        first.ContentHtml.Should().Contain("<strong>content</strong>", "content:encoded beats description");
        first.Published.Should().Be(Utc(2026, 11, 10, 7, 30));
        first.ThumbnailUrl.Should().Be("https://blog.example.com/wp-content/a-thumb.jpg");

        var second = feed.Items[1];
        second.ContentHtml.Should().Be("Plain description only");
        second.ThumbnailUrl.Should().Be("https://blog.example.com/wp-content/b.png", "image enclosure is the fallback thumbnail");
    }

    [Fact]
    public void Bad_dates_never_fail_the_feed()
    {
        var items = Parse("rss2-bad-dates.xml").Items.ToDictionary(i => i.Id!);

        items["d1"].Published.Should().Be(Utc(2026, 11, 4, 15), "EST is UTC-5");
        items["d2"].Published.Should().Be(Utc(2026, 11, 4, 9), "no day name, +0100");
        items["d3"].Published.Should().Be(Utc(2026, 11, 4, 10), "ISO 8601 in pubDate");
        items["d4"].Published.Should().Be(Utc(2026, 11, 4, 10), "two-digit year");
        items["d5"].Published.Should().Be(Utc(2026, 11, 4, 10), "wrong day name is ignored");
        items["d6"].Published.Should().BeNull("garbage is treated as missing");
        items["d7"].Published.Should().Be(Utc(2026, 11, 4, 8), "dc:date fallback");
        items["d8"].Published.Should().Be(Utc(2026, 11, 4, 10), "full day and month names");
        items["d9"].Published.Should().Be(Utc(2100, 1, 1, 0), "future dates are clamped at ingest, not here");
        items["d10"].Published.Should().Be(Utc(2026, 11, 4, 10), "single-digit day, no seconds");
    }

    [Fact]
    public void Guid_that_is_not_a_url_does_not_become_the_link()
    {
        Parse("rss2-bad-dates.xml").Items.Should().OnlyContain(i => i.Url == null);
    }

    [Fact]
    public void Missing_guids_leave_id_null_and_keep_links()
    {
        var items = Parse("rss2-missing-guids.xml").Items;

        items.Should().HaveCount(4);
        items.Should().OnlyContain(i => i.Id == null);
        items[0].Url.Should().Be("https://noguid.example.com/a");
        items[1].Url.Should().BeNull();
        items[1].Title.Should().Be("Title and date only");
        items[2].Url.Should().Be("https://noguid.example.com/c");
    }

    [Fact]
    public void Rss_091_with_netscape_doctype_parses()
    {
        var feed = Parse("rss091-netscape.xml");

        feed.Title.Should().Be("Old School");
        feed.Items.Should().ContainSingle().Which.Url.Should().Be("http://old.example.com/ancient.html");
    }

    [Fact]
    public void Relative_item_links_resolve_against_the_site_and_broken_ones_drop()
    {
        var items = Parse("rss2-relative-urls.xml").Items;

        items[0].Url.Should().Be("https://rel.example.com/blog/posts/1");
        items[1].Url.Should().BeNull();
    }

    [Fact]
    public void Bare_ampersands_and_html_entities_are_repaired()
    {
        var feed = Parse("rss2-html-entities.xml");

        feed.Title.Should().Be("Fish & Chips Weekly");
        feed.Items[0].Title.Should().Be("Salt — and vinegar");
        feed.Items[0].Url.Should().Be("https://fish.example.com/?a=1&b=2");
    }

    [Fact]
    public void Windows_1252_declared_encoding_is_honoured()
    {
        var feed = Parse("rss2-windows1252.xml");

        feed.Title.Should().Be("Café Crème");
        feed.Items[0].Title.Should().Be("Résumé — naïve");
    }

    [Fact]
    public void Utf8_label_on_windows_1252_bytes_falls_back()
    {
        var feed = Parse("rss2-mislabelled-utf8.xml");

        feed.Title.Should().Be("Mislabelled Café");
        feed.Items[0].Title.Should().Be("Naïve résumé");
    }

    [Fact]
    public void Bom_leading_whitespace_and_control_characters_are_tolerated()
    {
        var feed = Parse("rss2-control-chars-bom-whitespace.xml");

        feed.Title.Should().Be("Control chars");
        feed.Items[0].Title.Should().Be("Item one");
    }

    [Fact]
    public void Atom_resolves_xml_base_and_reads_html_and_xhtml_content()
    {
        var feed = Parse("atom.xml");

        feed.Title.Should().Be("Atom Example");
        feed.SiteUrl.Should().Be("https://atom.example.com/");
        feed.HubUrl.Should().Be("https://hub.example.com/");
        feed.SelfUrl.Should().Be("https://atom.example.com/feed.atom");

        var first = feed.Items[0];
        first.Title.Should().Be("Atom HTML title");
        first.Url.Should().Be("https://atom.example.com/posts/1");
        first.Author.Should().Be("Ann Author");
        first.Published.Should().Be(Utc(2026, 11, 4, 17, 30).AddSeconds(2));
        first.Updated.Should().Be(Utc(2026, 11, 5, 9));
        first.ContentHtml.Should().Contain("<a href=\"rel/link\">");

        var second = feed.Items[1];
        second.ContentHtml.Should().Contain("<em>body</em>");
        second.Published.Should().Be(Utc(2026, 11, 3, 10), "updated is the fallback when published is missing");
    }

    [Fact]
    public void Rdf_rss1_reads_sibling_items()
    {
        var feed = Parse("rdf-rss1.xml");

        feed.Title.Should().Be("RDF Site");
        var item = feed.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be("https://rdf.example.com/1");
        item.Author.Should().Be("Rae Dee");
        item.ContentHtml.Should().Be("<p>Rich <b>content</b></p>");
        item.Published.Should().Be(Utc(2026, 11, 2, 8));
    }

    [Fact]
    public void Json_feed_1_1_reads_items_hubs_and_numeric_ids()
    {
        var feed = Parse("jsonfeed-1.1.json");

        feed.Title.Should().Be("JSON Feed Example");
        feed.HubUrl.Should().Be("https://hub.example.com/");
        feed.SelfUrl.Should().Be("https://json.example.com/feed.json");

        var first = feed.Items[0];
        first.Author.Should().Be("Jay Son");
        first.Published.Should().Be(Utc(2026, 11, 1, 15));
        first.ThumbnailUrl.Should().Be("https://json.example.com/hero.png");

        var second = feed.Items[1];
        second.Id.Should().Be("2");
        second.Title.Should().BeEmpty();
        second.ContentHtml.Should().Be("<p>Text only item, no title. Line two.</p>");
        second.Published.Should().BeNull();
    }

    [Fact]
    public void Json_feed_1_0_single_author()
    {
        Parse("jsonfeed-1.0.json").Items.Should().ContainSingle().Which.Author.Should().Be("Solo Author");
    }

    [Fact]
    public void External_entity_cannot_read_local_files()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"skimr-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xml = System.Text.Encoding.UTF8.GetString(Fixtures.Feed("xxe-external-entity.xml"))
                .Replace("__SECRET_PATH__", secret.Replace('\\', '/'), StringComparison.Ordinal);

            var feed = FeedParser.Parse(System.Text.Encoding.UTF8.GetBytes(xml), new Uri("https://evil.example.com/feed"));

            feed.Title.Should().NotContain("TOP-SECRET");
            feed.Items.Should().OnlyContain(i => !i.Title.Contains("TOP-SECRET"));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Billion_laughs_does_not_expand()
    {
        var feed = Parse("billion-laughs.xml");

        feed.Title.Should().NotBeNull();
        feed.Title!.Length.Should().BeLessThan(100);
    }

    [Fact]
    public void Html_page_is_not_a_feed()
    {
        var act = () => Parse("not-a-feed.html");

        act.Should().Throw<FeedParseException>();
    }
}
