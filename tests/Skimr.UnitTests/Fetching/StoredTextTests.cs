using Skimr.Infrastructure.Fetching;

namespace Skimr.UnitTests.Fetching;

public sealed class StoredTextTests
{
    [Fact]
    public void Control_characters_are_removed_but_whitespace_kept()
    {
        StoredText.Fit("a\0b\u0001c\td\ne\rf", 100).Should().Be("abc\td\ne\rf");
    }

    [Fact]
    public void Long_text_is_cut_to_the_limit()
    {
        StoredText.Fit("abcdef", 3).Should().Be("abc");
        StoredText.Fit("abc", 3).Should().Be("abc");
        StoredText.Fit(null, 3).Should().BeNull();
    }

    [Fact]
    public void A_surrogate_pair_is_never_split()
    {
        // "ab" followed by U+1F600 (two UTF-16 code units): cutting at 3 would leave half an emoji.
        StoredText.Fit("ab\U0001F600", 3).Should().Be("ab");
    }
}
