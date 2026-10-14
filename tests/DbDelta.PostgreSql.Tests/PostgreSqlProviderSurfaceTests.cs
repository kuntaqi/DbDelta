using DbDelta.Core.Data;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// The point of the stub is that a second class covers the whole provider surface against an unchanged Core.
// These tests prove the surface is covered — every factory hands back the right interface, and every member
// that is not the quoter refuses with NotSupportedException rather than returning something that reads real.
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
    }

    // The stub members throw synchronously (expression-bodied throw), so the exception surfaces at the call
    // and a void lambda is the right shape — the discard keeps the returned task/enumerable from binding to
    // Assert.Throws's async overload.
    [Fact]
    public void Server_level_members_refuse()
    {
        Assert.Throws<NotSupportedException>(() => { _ = _provider.ProbeAsync(AnyConnectionString); });
        Assert.Throws<NotSupportedException>(() => { _ = _provider.ListDatabasesAsync(AnyConnectionString); });
        Assert.Throws<NotSupportedException>(() => { _ = _provider.DescribeDatabaseAsync(AnyConnectionString); });
    }

    [Fact]
    public void The_remaining_stub_member_refuses()
    {
        // Real now and exercised elsewhere: the schema reader, the schema and data script emitters, and the
        // data-compare read path. The collation fact reader is the last stub — CollationFact is SQL-Server-shaped
        // and resolving PostgreSQL collation sensitivity faithfully is a modelling task, not a port.
        Assert.Throws<NotSupportedException>(() => { _ = _provider.CreateCollationFactReader(AnyConnectionString).ReadAsync(null!); });
    }
}
