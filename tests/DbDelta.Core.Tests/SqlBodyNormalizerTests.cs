using DbDelta.Core.Comparison;

namespace DbDelta.Core.Tests;

public sealed class SqlBodyNormalizerTests
{
    [Theory]
    [InlineData("SELECT  a", "SELECT a")]
    [InlineData("SELECT\n\ta", "SELECT a")]
    [InlineData("  SELECT a  ", "SELECT a")]
    [InlineData("SELECT a\r\nFROM t", "SELECT a FROM t")]
    public void Insignificant_whitespace_collapses(string input, string expected) =>
        Assert.Equal(expected, SqlBodyNormalizer.Normalize(input));

    [Fact]
    public void Whitespace_inside_a_string_literal_is_preserved()
    {
        const string body = "SELECT  N'two  spaces'  FROM t";

        var normalized = SqlBodyNormalizer.Normalize(body);

        Assert.Equal("SELECT N'two  spaces' FROM t", normalized);
    }

    [Fact]
    public void Bodies_differing_only_by_layout_are_equivalent()
    {
        Assert.True(SqlBodyNormalizer.AreEquivalent("SELECT a,\n  b", "SELECT   a, b", ignoreWhitespace: true));
    }

    [Fact]
    public void Layout_differences_matter_when_the_option_is_off()
    {
        Assert.False(SqlBodyNormalizer.AreEquivalent("SELECT a, b", "SELECT  a, b", ignoreWhitespace: false));
    }

    [Fact]
    public void A_real_difference_is_never_normalized_away()
    {
        Assert.False(SqlBodyNormalizer.AreEquivalent("SELECT a", "SELECT b", ignoreWhitespace: true));
    }
}
