using DbDelta.Core.Model;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// No database: the digest and literal helpers are pure SQL-text builders, and the shape they produce is what
// the readers' correctness rests on, so it is pinned here as well as exercised live.
public sealed class PgRowDigestTests
{
    [Fact]
    public void No_columns_concatenate_to_an_empty_string_literal()
    {
        Assert.Equal("''", PgRowDigest.Concatenation([]));
    }

    [Fact]
    public void A_column_is_length_prefixed_and_null_tagged()
    {
        var sql = PgRowDigest.Concatenation(["id"]);
        Assert.Equal("'|' || coalesce(length((\"id\")::text)::text, '-') || ':' || coalesce((\"id\")::text, '')", sql);
    }

    [Fact]
    public void Several_columns_are_joined_so_order_and_boundaries_survive()
    {
        var sql = PgRowDigest.Concatenation(["a", "b"]);
        Assert.Contains("(\"a\")::text", sql, StringComparison.Ordinal);
        Assert.Contains("(\"b\")::text", sql, StringComparison.Ordinal);
        Assert.Contains(" || ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hash_wraps_the_concatenation_in_md5()
    {
        Assert.StartsWith("md5(", PgRowDigest.Hash(["id"]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_value_renders_as_the_null_keyword()
    {
        Assert.Equal("NULL", PgLiteral.For(new DataTypeSpec("integer"), null));
    }

    [Fact]
    public void A_value_is_quoted_and_cast_back_to_its_type()
    {
        Assert.Equal("'10.00'::numeric(10,2)", PgLiteral.For(new DataTypeSpec("numeric", Precision: 10, Scale: 2), "10.00"));
    }

    [Fact]
    public void A_quote_in_a_value_is_doubled()
    {
        Assert.Equal("'O''Brien'::text", PgLiteral.For(new DataTypeSpec("text"), "O'Brien"));
    }
}
