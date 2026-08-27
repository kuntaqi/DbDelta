namespace DbDelta.SqlServer.Tests;

// No database needed: this is the string rewrite that turns a stored definition into one that works whether
// or not the object is already there. It was wrong for months in a way no fixture could show, because every
// definition written by hand for a test starts with the word CREATE. Real ones very often do not.
public sealed class CreateOrAlterTests
{
    [Fact]
    public void A_definition_that_starts_with_create_is_rewritten()
    {
        Assert.Equal(
            "CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS x",
            TSqlWriter.CreateOrAlter("CREATE VIEW dbo.v AS SELECT 1 AS x"));
    }

    // The shape SSMS writes. This is what 11 of 38 affected objects in one real database looked like.
    [Fact]
    public void A_block_comment_before_the_create_does_not_hide_it()
    {
        var definition = "/****** Object:  View dbo.v    Script Date: 1/1/2026 ******/\nCREATE VIEW dbo.v AS SELECT 1 AS x";

        var rewritten = TSqlWriter.CreateOrAlter(definition);

        Assert.Contains("CREATE OR ALTER VIEW", rewritten, StringComparison.Ordinal);
        Assert.StartsWith("/****** Object:", rewritten, StringComparison.Ordinal);
    }

    // And the other 27: a hand-written note above the object.
    [Fact]
    public void A_line_comment_before_the_create_does_not_hide_it()
    {
        var definition = "-- This view exists because the report needed it\nCREATE VIEW dbo.v AS SELECT 1 AS x";

        var rewritten = TSqlWriter.CreateOrAlter(definition);

        Assert.Contains("CREATE OR ALTER VIEW", rewritten, StringComparison.Ordinal);
        Assert.StartsWith("-- This view exists", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_comments_and_blank_lines_are_all_skipped()
    {
        var definition = "\n-- one\n\n/* two */\n   -- three\nCREATE PROCEDURE dbo.p AS SELECT 1";

        Assert.Contains("CREATE OR ALTER PROCEDURE", TSqlWriter.CreateOrAlter(definition), StringComparison.Ordinal);
    }

    // T-SQL block comments nest, so scanning for the first "*/" would stop inside the outer one and land on
    // text that is still commented out.
    [Fact]
    public void A_nested_block_comment_is_counted_rather_than_scanned_for_its_first_close()
    {
        var definition = "/* outer /* inner */ still outer */\nCREATE FUNCTION dbo.f() RETURNS INT AS BEGIN RETURN 1 END";

        Assert.Contains("CREATE OR ALTER FUNCTION", TSqlWriter.CreateOrAlter(definition), StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_is_kept_rather_than_stripped()
    {
        var rewritten = TSqlWriter.CreateOrAlter("-- keep me\nCREATE VIEW dbo.v AS SELECT 1");

        Assert.Contains("-- keep me", rewritten, StringComparison.Ordinal);
    }

    // Only the leading comments are skipped. A CREATE that appears later in the text is not the statement
    // being rewritten, and treating it as one would corrupt the body.
    [Fact]
    public void A_create_that_is_not_the_first_statement_is_left_alone()
    {
        const string definition = "ALTER VIEW dbo.v AS SELECT 'CREATE VIEW' AS x";

        Assert.Equal(definition, TSqlWriter.CreateOrAlter(definition));
    }

    [Theory]
    [InlineData("-- only a comment")]
    [InlineData("/* only a comment */")]
    [InlineData("   ")]
    public void Text_with_no_statement_in_it_comes_back_unchanged(string definition)
    {
        Assert.Equal(definition.TrimStart(), TSqlWriter.CreateOrAlter(definition));
    }
}
