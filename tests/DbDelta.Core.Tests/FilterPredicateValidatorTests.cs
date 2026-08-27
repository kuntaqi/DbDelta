using DbDelta.Core.Data;

namespace DbDelta.Core.Tests;

public sealed class FilterPredicateValidatorTests
{
    private static readonly string[] Columns = ["CompanyId", "CompanyName", "Segment", "RatingBand", "Total"];

    [Theory]
    [InlineData("Segment = 'Retail'")]
    [InlineData("RatingBand >= 3")]
    [InlineData("Categoryish IS NULL", false)]
    [InlineData("CompanyName LIKE 'A%'")]
    [InlineData("RatingBand BETWEEN 1 AND 5")]
    [InlineData("CompanyId IN (1, 2, 3)")]
    [InlineData("Segment IS NOT NULL AND RatingBand > 2")]
    [InlineData("(Segment = 'Retail' OR Segment = 'Energy') AND Total > 100.50")]
    [InlineData("[Segment] = N'Retail'")]
    [InlineData("Segment = 'O''Brien'")]
    [InlineData("Total * 2 > 100")]
    public void A_predicate_of_columns_and_constants_is_accepted(string predicate, bool expected = true)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.Equal(expected, result.IsValid);
    }

    // The guarantee: a compare reads. A predicate that could start a second statement would run arbitrary
    // SQL against the target during a compare, which is where no guard is looking.
    [Fact]
    public void A_second_statement_is_refused()
    {
        var result = FilterPredicateValidator.Validate("1=1; DROP TABLE dbo.Company", Columns);

        Assert.False(result.IsValid);
        Assert.Contains("second statement", result.Error);
    }

    [Theory]
    [InlineData("Segment = 'x' -- and the rest")]
    [InlineData("Segment = 'x' /* hidden */")]
    public void A_comment_is_refused_because_it_can_hide_one(string predicate)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.False(result.IsValid);
        Assert.Contains("comment", result.Error);
    }

    // Every identifier has to be a column, which is what makes a function call, a subquery and a second
    // statement all fail on the same rule rather than needing three of their own.
    [Theory]
    [InlineData("GETDATE() > Total")]
    [InlineData("CompanyId IN (SELECT CompanyId FROM dbo.Other)")]
    [InlineData("EXISTS (SELECT 1)")]
    [InlineData("Segment = 'x' AND EXEC sp_who")]
    public void Anything_that_is_not_a_column_is_refused(string predicate)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.False(result.IsValid);
        Assert.Contains("is not a column", result.Error);
    }

    // The most likely mistake by far, so the refusal says what is on the table instead of only what is not.
    [Fact]
    public void An_unknown_column_is_named_alongside_the_ones_that_exist()
    {
        var result = FilterPredicateValidator.Validate("Segmnet = 'Retail'", Columns);

        Assert.False(result.IsValid);
        Assert.Contains("'Segmnet'", result.Error);
        Assert.Contains("CompanyName", result.Error);
    }

    [Theory]
    [InlineData("@x = 1")]
    [InlineData("Segment = \"Retail\"")]
    [InlineData("Segment = 'x' & 1")]
    public void A_character_that_has_no_place_in_a_filter_is_refused(string predicate)
    {
        Assert.False(FilterPredicateValidator.Validate(predicate, Columns).IsValid);
    }

    [Theory]
    [InlineData("(Segment = 'Retail'")]
    [InlineData("Segment = 'Retail')")]
    [InlineData("Segment = 'unclosed")]
    [InlineData("[Segment = 'x'")]
    public void An_unbalanced_predicate_is_refused_before_it_reaches_the_server(string predicate)
    {
        Assert.False(FilterPredicateValidator.Validate(predicate, Columns).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_filter_is_refused_rather_than_treated_as_all_rows(string? predicate)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.False(result.IsValid);
        Assert.Contains("needs a predicate", result.Error);
    }

    // Grammar is the server's question. This is nonsense SQL made only of things the alphabet allows, and
    // the validator lets it through on purpose — SQL Server rejects it with a better message.
    [Fact]
    public void Well_formedness_is_left_to_the_server()
    {
        Assert.True(FilterPredicateValidator.Validate("Segment Segment AND AND", Columns).IsValid);
    }
}
