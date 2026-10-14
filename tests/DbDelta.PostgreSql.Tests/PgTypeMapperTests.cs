using DbDelta.Core.Model;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// No database: these pin the dialect conventions the comparer depends on — canonical lowercase type names
// rendered through DataTypeSpec.ToString(), fractional-seconds precision in Precision, and the two enum maps.
public sealed class PgTypeMapperTests
{
    [Fact]
    public void Varchar_carries_its_length()
    {
        var spec = PgTypeMapper.Map("character varying", 50, null, null, null);
        Assert.Equal("character varying(50)", spec.ToString());
    }

    [Fact]
    public void Numeric_carries_precision_and_scale()
    {
        var spec = PgTypeMapper.Map("numeric", null, 10, 2, null);
        Assert.Equal("numeric(10,2)", spec.ToString());
    }

    [Fact]
    public void Integer_ignores_the_reported_numeric_precision()
    {
        // information_schema reports numeric_precision 32 for integer; it is inherent, not written.
        var spec = PgTypeMapper.Map("integer", null, 32, 0, null);
        Assert.Equal("integer", spec.ToString());
    }

    [Fact]
    public void Double_precision_ignores_the_reported_numeric_precision()
    {
        var spec = PgTypeMapper.Map("double precision", null, 53, null, null);
        Assert.Equal("double precision", spec.ToString());
    }

    [Theory]
    [InlineData("timestamp with time zone", "timestamp with time zone(6)")]
    [InlineData("time without time zone", "time without time zone(3)")]
    public void Temporal_types_put_fractional_seconds_in_precision(string name, string expected)
    {
        var precision = expected.EndsWith("(6)", StringComparison.Ordinal) ? 6 : 3;
        var spec = PgTypeMapper.Map(name, null, null, null, precision);
        Assert.Equal(expected, spec.ToString());
    }

    [Fact]
    public void A_user_defined_type_keeps_its_schema_and_no_length()
    {
        var spec = PgTypeMapper.Map("mood", null, null, null, null, isUserDefined: true, schema: "app");
        Assert.True(spec.IsUserDefined);
        Assert.Equal("app", spec.Schema);
        Assert.Equal("app.mood", spec.ToString());
    }

    [Theory]
    [InlineData("c", ReferentialAction.Cascade)]
    [InlineData("n", ReferentialAction.SetNull)]
    [InlineData("d", ReferentialAction.SetDefault)]
    [InlineData("a", ReferentialAction.NoAction)]
    [InlineData("r", ReferentialAction.NoAction)]
    public void Referential_actions_map_including_restrict_to_no_action(string code, ReferentialAction expected)
    {
        Assert.Equal(expected, PgTypeMapper.MapReferentialAction(code));
    }

    [Theory]
    [InlineData("hash", IndexKind.Hash)]
    [InlineData("btree", IndexKind.Rowstore)]
    [InlineData("gin", IndexKind.Rowstore)]
    [InlineData("gist", IndexKind.Rowstore)]
    public void Index_access_methods_map_to_a_kind(string accessMethod, IndexKind expected)
    {
        Assert.Equal(expected, PgTypeMapper.MapIndexKind(accessMethod));
    }
}
