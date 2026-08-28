using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.SqlServer.Tests;

// The last hole in closure. A procedure taking a table-valued parameter needs that table type to exist
// before it can compile, and picking the procedure alone gave a script that failed.
//
// The reason was not that SQL Server keeps this quiet. sys.sql_expression_dependencies reports the type
// perfectly well — it just reports it as referenced_class 6, where referenced_id is a user_type_id rather
// than an object_id. The reader joined every row to sys.objects on that id, so type rows matched nothing
// and vanished, and the join was comparing two different id spaces to do it.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollection))]
public sealed class RoutineTypeClosureTests
{
    private readonly LocalDbFixture _fixture;

    public RoutineTypeClosureTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_procedure_records_the_table_type_its_parameter_uses()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var source = await ReadSourceAsync();
        var procedure = source.Routines.Single(r => r.Identity.Name == "usp_TagCompanies");

        Assert.Contains(
            procedure.DependsOn,
            d => d.Type == ObjectType.UserDefinedType && d.Name == "IdList");

        // The alias type too: it is named in the parameter list and reported the same way.
        Assert.Contains(
            procedure.DependsOn,
            d => d.Type == ObjectType.UserDefinedType && d.Name == "PhoneNumber");

        // And the object dependency it always had, so the type branch did not cost the object branch.
        Assert.Contains(procedure.DependsOn, d => d.Type == ObjectType.Table && d.Name == "Company");
    }

    // The point of all of it: picking the procedure on its own pulls the type in.
    [SkippableFact]
    public async Task Picking_the_procedure_requires_the_type_it_takes()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();
        var procedure = source.Routines.Single(r => r.Identity.Name == "usp_TagCompanies").Identity;

        var closure = SchemaClosure.Expand(
            source, target, diff, new HashSet<ObjectIdentity> { procedure });

        var required = closure.Required.Select(r => r.Identity).ToList();

        Assert.Contains(required, i => i.Type == ObjectType.UserDefinedType && i.Name == "IdList");
        Assert.Contains(required, i => i.Type == ObjectType.UserDefinedType && i.Name == "PhoneNumber");
    }

    // A type the target already has is not a prerequisite, or every plan would carry every type a routine
    // mentions. Closure asks what is missing, not what is referenced.
    [SkippableFact]
    public async Task A_type_the_target_already_has_is_not_required()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "RoutineType";

        // A target that already holds both types, so only the procedure itself is left to do.
        var targetConnection = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            await ExecuteAsync(targetConnection, "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL PRIMARY KEY);");
            await ExecuteAsync(targetConnection, "CREATE TYPE dbo.PhoneNumber FROM NVARCHAR(20) NOT NULL;");

            var source = await ReadSourceAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            var procedure = source.Routines.Single(r => r.Identity.Name == "usp_TagCompanies").Identity;

            var closure = SchemaClosure.Expand(
                source, target, diff, new HashSet<ObjectIdentity> { procedure });

            Assert.DoesNotContain(
                closure.Required.Select(r => r.Identity),
                i => i.Type == ObjectType.UserDefinedType);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A type in DependsOn must not disturb the order the programmables are emitted in: it is not a
    // programmable, so it is not a node in that sort. The sorter skips edges to nodes it does not hold,
    // and this is the check that it keeps doing so.
    [SkippableFact]
    public async Task A_type_dependency_does_not_break_programmable_ordering()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();
        var script = new TSqlEmitter().Emit(source, target, diff);
        var sql = script.ToSql();

        // The type is created in its own phase, well before anything that could use it.
        Assert.True(
            sql.IndexOf("CREATE TYPE [dbo].[IdList]", StringComparison.Ordinal)
                < sql.IndexOf("usp_TagCompanies", StringComparison.Ordinal),
            "the type has to exist before the procedure that takes it");

        // And a view that reads another view is still ordered behind it.
        Assert.True(
            sql.IndexOf("vCompanySegment", StringComparison.Ordinal)
                < sql.IndexOf("vActiveSegments", StringComparison.Ordinal),
            "body dependencies still order the programmables");
    }

    private static async Task<DatabaseSchema> ReadSourceAsync() =>
        await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();

    private static async Task<(DatabaseSchema Source, DatabaseSchema Target, SchemaDiff Diff)> CompareAsync()
    {
        var source = await ReadSourceAsync();
        var target = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase)).ReadAsync();

        return (source, target, new SchemaComparer().Compare(source, target));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
