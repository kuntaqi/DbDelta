using DbDelta.Core.Model;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// No database: the type renderer is the inverse of the reader's type mapping, and the one case worth pinning
// is temporal precision, where PostgreSQL DDL puts the precision before the time-zone words — unlike the
// engine-neutral DataTypeSpec.ToString().
public sealed class PgTypeTextTests
{
    [Fact]
    public void Varchar_renders_its_length()
    {
        Assert.Equal("character varying(50)", PgTypeText.Declare(new DataTypeSpec("character varying", MaxLength: 50)));
    }

    [Fact]
    public void Numeric_renders_precision_and_scale()
    {
        Assert.Equal("numeric(10,2)", PgTypeText.Declare(new DataTypeSpec("numeric", Precision: 10, Scale: 2)));
    }

    [Theory]
    [InlineData("timestamp with time zone", 6, "timestamp(6) with time zone")]
    [InlineData("time without time zone", 3, "time(3) without time zone")]
    public void Temporal_precision_goes_before_the_time_zone_words(string name, int precision, string expected)
    {
        Assert.Equal(expected, PgTypeText.Declare(new DataTypeSpec(name, Precision: precision)));
    }

    [Fact]
    public void A_user_defined_type_is_quoted_and_qualified()
    {
        var spec = new DataTypeSpec("mood", IsUserDefined: true, Schema: "app");
        Assert.Equal("\"app\".\"mood\"", PgTypeText.Declare(spec));
    }

    [Theory]
    [InlineData(IndexKind.Rowstore, "btree")]
    [InlineData(IndexKind.Hash, "hash")]
    [InlineData(IndexKind.Gin, "gin")]
    [InlineData(IndexKind.Gist, "gist")]
    [InlineData(IndexKind.Brin, "brin")]
    [InlineData(IndexKind.Spgist, "spgist")]
    public void Index_methods_map_to_their_access_method(IndexKind kind, string expected)
    {
        Assert.Equal(expected, PgTypeText.IndexMethod(kind));
    }

    [Theory]
    [InlineData(ReferentialAction.Cascade, "CASCADE")]
    [InlineData(ReferentialAction.SetNull, "SET NULL")]
    [InlineData(ReferentialAction.SetDefault, "SET DEFAULT")]
    public void Referential_actions_render(ReferentialAction action, string expected)
    {
        Assert.Equal(expected, PgTypeText.ReferentialAction(action));
    }

    [Fact]
    public void No_action_renders_as_null_so_the_clause_is_omitted()
    {
        Assert.Null(PgTypeText.ReferentialAction(ReferentialAction.NoAction));
    }
}
