using DbDelta.Api.Services;
using DbDelta.Core.Apply;
using DbDelta.PostgreSql;
using DbDelta.SqlServer;

namespace DbDelta.Api.Tests;

// No database: the factory and the engine-normaliser are the selection the whole provider-per-connection change
// rests on. A null engine must still pick SQL Server so an older client behaves exactly as before.
public sealed class DatabaseProviderFactoryTests
{
    private readonly DatabaseProviderFactory _factory = new([new SqlServerProvider(), new PostgreSqlProvider()]);

    [Theory]
    [InlineData(null, "sqlserver")]
    [InlineData("", "sqlserver")]
    [InlineData("sqlserver", "sqlserver")]
    [InlineData("MSSQL", "sqlserver")]
    [InlineData("postgresql", "postgresql")]
    [InlineData("postgres", "postgresql")]
    [InlineData("PG", "postgresql")]
    public void Normalize_maps_aliases_and_defaults_to_sql_server(string? engine, string expected)
    {
        Assert.Equal(expected, DatabaseEngine.Normalize(engine));
    }

    [Fact]
    public void Unknown_engine_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseEngine.Normalize("oracle"));
    }

    [Fact]
    public void A_null_engine_resolves_to_the_sql_server_provider()
    {
        Assert.IsType<SqlServerProvider>(_factory.For(null));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    [InlineData("pg")]
    public void A_postgres_engine_resolves_to_the_postgres_provider(string engine)
    {
        Assert.IsType<PostgreSqlProvider>(_factory.For(engine));
    }

    [Fact]
    public void Each_provider_hands_back_its_own_executor()
    {
        Assert.IsType<SqlServerScriptExecutor>(_factory.For("sqlserver").CreateScriptExecutor());
        Assert.IsType<PostgreSqlScriptExecutor>(_factory.For("postgresql").CreateScriptExecutor());
    }

    [Fact]
    public void An_unregistered_engine_cannot_be_resolved()
    {
        Assert.Throws<InvalidOperationException>(() => _factory.For("oracle"));
    }
}
