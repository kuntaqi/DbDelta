using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// A second class covers the whole provider surface against an unchanged Core. Once a compile-proof whose members
// threw, now a complete provider: these tests prove every factory hands back the right interface. The behaviour
// of each implementation is proved against a live server in its own test class.
public sealed class PostgreSqlProviderSurfaceTests
{
    private const string AnyConnectionString = "Host=localhost;Database=app";

    private readonly PostgreSqlProvider _provider = new();

    [Fact]
    public void Key_identifies_the_engine()
    {
        Assert.Equal("postgresql", _provider.Key);
    }

    [Fact]
    public void Quoter_is_the_real_postgres_quoter()
    {
        Assert.IsType<PostgreSqlQuoter>(_provider.Quoter);
        Assert.Same(PostgreSqlQuoter.Instance, _provider.Quoter);
    }

    [Fact]
    public void Factories_return_the_expected_interface()
    {
        Assert.IsAssignableFrom<ISchemaReader>(_provider.CreateSchemaReader(AnyConnectionString));
        Assert.IsAssignableFrom<IScriptEmitter>(_provider.CreateScriptEmitter());
        Assert.IsAssignableFrom<IVolumeReader>(_provider.CreateVolumeReader(AnyConnectionString));
        Assert.IsAssignableFrom<IRowHashReader>(_provider.CreateRowHashReader(AnyConnectionString));
        Assert.IsAssignableFrom<IRowDetailReader>(_provider.CreateRowDetailReader(AnyConnectionString));
        Assert.IsAssignableFrom<IRowByValueReader>(_provider.CreateRowByValueReader(AnyConnectionString));
        Assert.IsAssignableFrom<IDataScriptEmitter>(_provider.CreateDataScriptEmitter());
        Assert.IsAssignableFrom<ITableFingerprintReader>(_provider.CreateFingerprintReader(AnyConnectionString));
        Assert.IsAssignableFrom<IKeyUniquenessChecker>(_provider.CreateKeyUniquenessChecker(AnyConnectionString));
        Assert.IsAssignableFrom<ICollationFactReader>(_provider.CreateCollationFactReader(AnyConnectionString));
        Assert.IsAssignableFrom<IScriptExecutor>(_provider.CreateScriptExecutor());
    }
}
