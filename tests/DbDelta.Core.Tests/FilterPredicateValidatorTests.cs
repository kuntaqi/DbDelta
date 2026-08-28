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

    // A subquery and a second statement still fail on the identifier rule, which is what lets one rule cover
    // cases that would otherwise need three of their own.
    [Theory]
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

    // The motivating case. A year filter needed no new alphabet category, only permission to call a
    // function whose argument is an ordinary expression.
    [Theory]
    [InlineData("YEAR(Total) = 2026")]
    [InlineData("LEN(CompanyName) > 5")]
    [InlineData("UPPER(Segment) = 'RETAIL'")]
    [InlineData("LEFT(CompanyName, 3) = 'ABC'")]
    [InlineData("ISNULL(Segment, 'none') = 'none'")]
    [InlineData("ABS(Total) BETWEEN 1 AND 100")]
    [InlineData("SUBSTRING(CompanyName, 1, 3) IN ('ABC', 'DEF')")]
    [InlineData("ROUND(Total, 2) = 10.5")]
    // Nested, because nothing about the rule stops one expression being another's argument.
    [InlineData("LEN(LTRIM(RTRIM(CompanyName))) = 0")]
    [InlineData("COALESCE(Segment, CompanyName, 'x') = 'x'")]
    public void A_call_to_an_allowed_function_is_accepted(string predicate)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.True(result.IsValid, result.Error);
    }

    // The hazard specific to this tool, and the reason the list is not simply "functions that cannot write".
    // The predicate is embedded into two queries, against two databases, on two connections. Anything that
    // answers differently between them makes the merge join read rows as inserted and deleted.
    [Theory]
    [InlineData("Total > GETDATE()", "reads the clock")]
    [InlineData("Total > SYSDATETIME()", "reads the clock")]
    [InlineData("CompanyName = NEWID()", "different value every call")]
    [InlineData("Total > RAND()", "different value every call")]
    [InlineData("Segment = DB_NAME()", "different databases")]
    [InlineData("Segment = SUSER_NAME()", "login asking")]
    [InlineData("Segment = FORMAT(Total, 'n')", "culture")]
    public void A_function_that_answers_differently_on_the_two_sides_is_refused_with_the_reason(
        string predicate,
        string because)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.False(result.IsValid);
        Assert.Contains(because, result.Error, StringComparison.Ordinal);
        Assert.Contains("once against each database", result.Error, StringComparison.Ordinal);
    }

    // CURRENT_TIMESTAMP takes no brackets, so it arrives as a bare word rather than a call. It still has to
    // be refused for the reason it is refused, not as an unknown column.
    [Fact]
    public void A_niladic_clock_function_without_brackets_is_still_refused_for_being_one()
    {
        var result = FilterPredicateValidator.Validate("Total > CURRENT_TIMESTAMP", Columns);

        Assert.False(result.IsValid);
        Assert.Contains("reads the clock", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OPENROWSET('x', 'y', 'z') = 1", "outside this database")]
    [InlineData("OPENQUERY(srv, 'select 1') = 1", "outside this database")]
    public void A_function_that_reaches_outside_the_database_is_refused(string predicate, string because)
    {
        var result = FilterPredicateValidator.Validate(predicate, Columns);

        Assert.False(result.IsValid);
        Assert.Contains(because, result.Error, StringComparison.Ordinal);
    }

    // Reaching user code needs a schema qualifier, and a dot is not in the alphabet — so this fails on the
    // identifier rule before the allowlist is consulted at all. SQL Server agrees from the other direction:
    // an unqualified name gets "is not a recognized built-in function name".
    [Theory]
    [InlineData("dbo.Sneaky(CompanyId) = 1")]
    [InlineData("master.dbo.xp_cmdshell('dir') = 1")]
    public void A_schema_qualified_call_cannot_be_expressed_at_all(string predicate)
    {
        Assert.False(FilterPredicateValidator.Validate(predicate, Columns).IsValid);
    }

    [Fact]
    public void A_function_not_on_the_list_says_what_is_on_it()
    {
        var result = FilterPredicateValidator.Validate("SOUNDEX(CompanyName) = 'x'", Columns);

        Assert.False(result.IsValid);
        Assert.Contains("'SOUNDEX' is not a function a filter can use", result.Error, StringComparison.Ordinal);
        Assert.Contains("SUBSTRING", result.Error, StringComparison.Ordinal);
        Assert.Contains("DATEADD", result.Error, StringComparison.Ordinal);
    }

    // The allowlist stops at functions whose arguments are ordinary expressions. DATEADD and CAST each take
    // a bare word that is not a column, and admitting them would mean widening the alphabet.
    [Theory]
    [InlineData("DATEADD(day, -1, Total) > 1")]
    [InlineData("DATEPART(year, Total) = 2026")]
    [InlineData("CAST(Total AS INT) = 1")]
    [InlineData("CONVERT(varchar, Total) = '1'")]
    public void A_function_taking_a_bare_word_stays_out(string predicate)
    {
        Assert.False(FilterPredicateValidator.Validate(predicate, Columns).IsValid);
    }

    // A function name on its own is an identifier, and there is no column called LEN.
    [Fact]
    public void An_allowed_name_that_is_not_being_called_is_still_not_a_column()
    {
        var result = FilterPredicateValidator.Validate("LEN > 5", Columns);

        Assert.False(result.IsValid);
        Assert.Contains("needs its argument in brackets", result.Error, StringComparison.Ordinal);
    }

    // A table is entitled to a column called Year, and reading the word as a function would refuse a filter
    // over a column that exists.
    [Fact]
    public void A_column_sharing_a_functions_name_is_read_as_the_column()
    {
        Assert.True(FilterPredicateValidator.Validate("Year = 2026", ["Year", "Total"]).IsValid);
        Assert.True(FilterPredicateValidator.Validate("Left = 'x'", ["Left"]).IsValid);
    }

    [Fact]
    public void The_allowed_list_is_available_for_a_message_to_quote()
    {
        Assert.Contains("YEAR", FilterPredicateValidator.AllowedFunctions);
        Assert.DoesNotContain("GETDATE", FilterPredicateValidator.AllowedFunctions);
        Assert.DoesNotContain("DATEADD", FilterPredicateValidator.AllowedFunctions);
    }
}
