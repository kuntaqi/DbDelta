using DbDelta.Core.Model;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

public sealed class PostgreSqlQuoterTests
{
    private readonly PostgreSqlQuoter _quoter = PostgreSqlQuoter.Instance;

    [Fact]
    public void Quote_wraps_in_double_quotes()
    {
        Assert.Equal("\"name\"", _quoter.Quote("name"));
    }

    [Fact]
    public void Quote_doubles_an_embedded_quote()
    {
        Assert.Equal("\"a\"\"b\"", _quoter.Quote("a\"b"));
    }

    [Fact]
    public void Qualify_quotes_schema_and_name_separately()
    {
        var identity = new ObjectIdentity(ObjectType.Table, "schema", "name");
        Assert.Equal("\"schema\".\"name\"", _quoter.Qualify(identity));
    }

    [Fact]
    public void Quote_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => _quoter.Quote(null!));
    }
}
