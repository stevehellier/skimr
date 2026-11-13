using Skimr.Domain;

namespace Skimr.UnitTests.Feeds;

public sealed class DedupKeyTests
{
    private static readonly DateTime When = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Guid_wins_over_link_and_title()
    {
        ArticleIdentity.DedupKey("g1", "https://a/1", "T", When)
            .Should().Be(ArticleIdentity.DedupKey("g1", "https://b/other", "Other", null));
    }

    [Fact]
    public void Link_is_used_when_guid_is_blank()
    {
        ArticleIdentity.DedupKey("  ", "https://a/1", "T", When)
            .Should().Be(ArticleIdentity.DedupKey(null, "https://a/1", "Different title", null));
    }

    [Fact]
    public void Title_and_date_are_the_last_resort()
    {
        var key = ArticleIdentity.DedupKey(null, null, "T", When);

        key.Should().Be(ArticleIdentity.DedupKey(null, "", "T", When));
        key.Should().NotBe(ArticleIdentity.DedupKey(null, null, "T", When.AddMinutes(1)));
    }

    [Fact]
    public void Guid_and_link_with_same_text_do_not_collide()
    {
        ArticleIdentity.DedupKey("https://a/1", null, null, null)
            .Should().NotBe(ArticleIdentity.DedupKey(null, "https://a/1", null, null));
    }

    [Fact]
    public void Key_is_64_lowercase_hex_chars()
    {
        ArticleIdentity.DedupKey("x", null, null, null).Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
