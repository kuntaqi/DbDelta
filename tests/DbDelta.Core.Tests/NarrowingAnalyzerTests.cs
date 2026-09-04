using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

// Which column changes can lose what is already in the target. The cases that matter most are the ones
// SQL Server does *not* refuse: narrowing decimal scale or datetime precision rounds every row and
// reports success, so nothing downstream would notice unless this says so first.
public sealed class NarrowingAnalyzerTests
{
    private static ColumnDefinition Col(string name, DataTypeSpec type, bool nullable = true) =>
        new() { Name = name, DataType = type, IsNullable = nullable };

    private static DataTypeSpec Text(string name, int length) => new(name, MaxLength: length);
    private static DataTypeSpec Dec(int precision, int scale) => new("decimal", Precision: precision, Scale: scale);

    [Fact]
    public void An_unchanged_column_loses_nothing()
    {
        var c = Col("Name", Text("nvarchar", 40));
        Assert.False(NarrowingAnalyzer.Analyse(c, c).LosesData);
    }

    [Fact]
    public void Widening_a_string_loses_nothing()
    {
        var v = NarrowingAnalyzer.Analyse(Col("Name", Text("nvarchar", 20)), Col("Name", Text("nvarchar", 80)));
        Assert.Equal(NarrowingKind.None, v.Kind);
    }

    [Fact]
    public void Shortening_a_string_narrows_and_says_what_does_not_fit()
    {
        var v = NarrowingAnalyzer.Analyse(Col("Name", Text("nvarchar", 80)), Col("Name", Text("nvarchar", 20)));
        Assert.Equal(NarrowingKind.Narrows, v.Kind);
        Assert.Contains("longer than 20 characters", v.Reason);
    }

    [Fact]
    public void Leaving_MAX_for_a_fixed_length_narrows()
    {
        var from = Col("Body", new DataTypeSpec("nvarchar", IsMax: true));
        var to = Col("Body", Text("nvarchar", 4000));
        Assert.Equal(NarrowingKind.Narrows, NarrowingAnalyzer.Analyse(from, to).Kind);
    }

    [Fact]
    public void Dropping_decimal_scale_narrows_and_says_the_server_will_not_refuse()
    {
        // The case that motivated all of this: 10.1234 becomes 10.12 and the apply reports success.
        var v = NarrowingAnalyzer.Analyse(Col("Price", Dec(18, 4)), Col("Price", Dec(18, 2)));
        Assert.Equal(NarrowingKind.Narrows, v.Kind);
        Assert.Contains("rounded to 2 decimal place(s)", v.Reason);
        Assert.Contains("does not refuse", v.Reason);
    }

    [Fact]
    public void Adding_decimal_scale_loses_nothing()
    {
        Assert.False(NarrowingAnalyzer.Analyse(Col("Price", Dec(18, 2)), Col("Price", Dec(18, 4))).LosesData);
    }

    [Fact]
    public void Dropping_datetime2_precision_narrows_and_calls_it_rounding_not_overflow()
    {
        // Shaped the way the reader actually shapes it: a temporal type's fractional-seconds digits land
        // in Precision, not Scale, so that it renders as DATETIME2(0) rather than DATETIME2(19,0). An
        // earlier version of this test used Scale and passed for the wrong reason — the live repro then
        // showed the precision branch firing with an "overflow" message, which is the wrong warning.
        var v = NarrowingAnalyzer.Analyse(
            Col("SeenAt", new DataTypeSpec("datetime2", Precision: 27)),
            Col("SeenAt", new DataTypeSpec("datetime2", Precision: 0)));

        Assert.Equal(NarrowingKind.Narrows, v.Kind);
        Assert.Contains("fractional seconds are rounded", v.Reason);
        Assert.Contains("does not refuse", v.Reason);
        Assert.DoesNotContain("overflow", v.Reason);
    }

    [Fact]
    public void Reducing_a_non_temporal_precision_still_reads_as_overflow()
    {
        var v = NarrowingAnalyzer.Analyse(Col("N", new DataTypeSpec("float", Precision: 53)), Col("N", new DataTypeSpec("float", Precision: 24)));
        Assert.Equal(NarrowingKind.Narrows, v.Kind);
        Assert.Contains("overflow", v.Reason);
    }

    [Fact]
    public void Reducing_decimal_precision_narrows()
    {
        var v = NarrowingAnalyzer.Analyse(Col("Total", Dec(18, 2)), Col("Total", Dec(9, 2)));
        Assert.Equal(NarrowingKind.Narrows, v.Kind);
        Assert.Contains("more than 9 digit(s)", v.Reason);
    }

    [Theory]
    [InlineData("bigint", "int")]
    [InlineData("int", "smallint")]
    [InlineData("smallint", "tinyint")]
    [InlineData("float", "real")]
    public void Stepping_down_a_numeric_ladder_narrows(string from, string to)
    {
        var v = NarrowingAnalyzer.Analyse(Col("N", new DataTypeSpec(from)), Col("N", new DataTypeSpec(to)));
        Assert.Equal(NarrowingKind.Narrows, v.Kind);
    }

    [Theory]
    [InlineData("int", "bigint")]
    [InlineData("real", "float")]
    public void Stepping_up_a_numeric_ladder_loses_nothing(string from, string to)
    {
        Assert.False(NarrowingAnalyzer.Analyse(Col("N", new DataTypeSpec(from)), Col("N", new DataTypeSpec(to))).LosesData);
    }

    [Fact]
    public void Unicode_to_non_unicode_narrows_even_when_it_gets_longer()
    {
        // Length is not the risk here; the code page is. A wider varchar still cannot hold every nchar.
        var v = NarrowingAnalyzer.Analyse(Col("Name", Text("nvarchar", 20)), Col("Name", Text("varchar", 200)));
        Assert.Equal(NarrowingKind.Narrows, v.Kind);
        Assert.Contains("code page", v.Reason);
    }

    [Fact]
    public void A_different_kind_of_type_is_reported_as_a_conversion()
    {
        var v = NarrowingAnalyzer.Analyse(Col("Value", Text("nvarchar", 40)), Col("Value", new DataTypeSpec("int")));
        Assert.Equal(NarrowingKind.Converts, v.Kind);
    }

    [Fact]
    public void Becoming_NOT_NULL_is_reported_even_when_the_type_is_unchanged()
    {
        var v = NarrowingAnalyzer.Analyse(
            Col("Name", Text("nvarchar", 40), nullable: true),
            Col("Name", Text("nvarchar", 40), nullable: false));

        Assert.Equal(NarrowingKind.Converts, v.Kind);
        Assert.Contains("existing NULL", v.Reason);
    }

    [Fact]
    public void A_user_defined_type_is_flagged_rather_than_guessed_at()
    {
        // An alias type's width lives in its own definition, so nothing can be concluded from the
        // reference. Saying so is honest; assuming either way is not.
        var v = NarrowingAnalyzer.Analyse(
            Col("Phone", Text("nvarchar", 40)),
            Col("Phone", new DataTypeSpec("PhoneNumber", IsUserDefined: true, Schema: "dbo")));

        Assert.Equal(NarrowingKind.Converts, v.Kind);
        Assert.Contains("user-defined type", v.Reason);
    }
}
