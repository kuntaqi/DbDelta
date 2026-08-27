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

    // The bug this class had for as long as it existed. English prose has apostrophes in it, and one of
    // them in a comment used to open a string literal that never closed, so every space after it was kept
    // verbatim. Which mattered because of the next test.
    [Fact]
    public void An_apostrophe_in_a_comment_does_not_open_a_string_literal()
    {
        Assert.True(SqlBodyNormalizer.AreEquivalent(
            "-- the report's owner asked for it\nSELECT a,  b",
            "-- the report's owner asked for it\nSELECT a, b",
            ignoreWhitespace: true));
    }

    // SQL Server does not store "CREATE OR ALTER". It blanks the OR ALTER out and leaves the gap, so what
    // comes back for an object this tool wrote is "CREATE   VIEW". Whitespace collapsing is the only reason
    // that compares clean — so anything that stops the collapsing makes the tool's own output look changed.
    [Fact]
    public void What_sql_server_stores_for_a_create_or_alter_compares_clean_against_the_source()
    {
        Assert.True(SqlBodyNormalizer.AreEquivalent(
            "-- a note with an apostrophe: don't remove\nCREATE VIEW dbo.v AS SELECT 1 AS x",
            "-- a note with an apostrophe: don't remove\nCREATE   VIEW dbo.v AS SELECT 1 AS x",
            ignoreWhitespace: true));
    }

    [Fact]
    public void An_apostrophe_in_a_block_comment_does_not_open_a_string_literal()
    {
        Assert.True(SqlBodyNormalizer.AreEquivalent(
            "/* it's fine */ SELECT  a",
            "/* it's fine */ SELECT a",
            ignoreWhitespace: true));
    }

    // A comment is still part of the definition. Changing what it says is a change.
    [Fact]
    public void A_comment_is_compared_rather_than_stripped()
    {
        Assert.False(SqlBodyNormalizer.AreEquivalent(
            "-- old note\nSELECT a",
            "-- new note\nSELECT a",
            ignoreWhitespace: true));
    }

    // Comment markers inside a literal are text, not comments, so what follows is still SQL.
    [Fact]
    public void A_comment_marker_inside_a_string_literal_starts_no_comment()
    {
        Assert.Equal(
            "SELECT '-- not a comment' AS x, b",
            SqlBodyNormalizer.Normalize("SELECT '-- not a comment' AS x,   b"));
    }
}
