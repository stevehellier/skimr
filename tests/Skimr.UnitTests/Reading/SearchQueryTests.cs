using Skimr.Infrastructure.Postgres;
using Skimr.Infrastructure.Sqlite;

namespace Skimr.UnitTests.Reading;

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData("rust async", "\"rust\" \"async\"")]
    [InlineData("kest*", "\"kest\"*")]
    [InlineData("say \"hi\"", "\"say\" \"\"\"hi\"\"\"")]
    [InlineData("NEAR(a b", "\"NEAR(a\" \"b\"")]
    [InlineData("title:x -y", "\"title:x\" \"-y\"")]
    [InlineData("AND OR", "\"AND\" \"OR\"")]
    public void Fts5_terms_are_quoted_so_operators_are_literal(string input, string expected)
    {
        SqliteArticleSearch.ToFtsQuery(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    [InlineData(null)]
    public void Empty_input_gives_no_fts5_query(string? input)
    {
        SqliteArticleSearch.ToFtsQuery(input).Should().BeNull();
    }

    [Fact]
    public void Fts5_query_is_capped_at_twelve_terms()
    {
        SqliteArticleSearch.ToFtsQuery(string.Join(' ', Enumerable.Range(0, 50).Select(i => $"t{i}")))!.Split(' ').Should().HaveCount(12);
    }

    [Theory]
    [InlineData("kest*", "kest:*")]
    [InlineData("rust kest*", "rust & kest:*")]
    [InlineData("a'b:* !x* (y)", "ab:* & x:* & y")]
    [InlineData("café*", "café:*")]
    public void Postgres_prefix_terms_keep_only_letters_and_digits(string input, string expected)
    {
        PostgresArticleSearch.ToPrefixQuery(input).Should().Be(expected);
    }

    [Fact]
    public void Postgres_prefix_query_of_only_symbols_is_empty()
    {
        PostgresArticleSearch.ToPrefixQuery("*** !!* ::*").Should().BeNull();
    }
}
