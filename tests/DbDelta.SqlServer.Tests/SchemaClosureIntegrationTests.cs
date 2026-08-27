using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.SqlServer.Tests;

// Closure is only worth anything if the script it produces runs. These go all the way to the server:
// one tick against an empty target, emitted and executed, with the target read back afterwards.
[Collection(nameof(LocalDbCollection))]
public sealed class SchemaClosureIntegrationTests
{
    private readonly LocalDbFixture _fixture;

    public SchemaClosureIntegrationTests(LocalDbFixture fixture) => _fixture = fixture;

    private static readonly ObjectIdentity Contact = new(ObjectType.Table, "dbo", "Contact");

    [SkippableFact]
    public async Task One_ticked_table_brings_its_parents_and_the_script_commits()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureOk";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            // dbo.Contact references dbo.Company, which references dbo.Category. Only the first is ticked.
            var closure = SchemaClosure.Expand(source, target, diff, [Contact]);

            // dbo.PhoneNumber comes along too: dbo.Contact.Phone is declared with it, and a column's type
            // is a prerequisite in the same way a foreign key's parent is.
            Assert.Equal(
                ["dbo.PhoneNumber", "dbo.Company", "dbo.Category"],
                closure.Required.Select(r => r.Identity.QualifiedName));
            Assert.Empty(closure.Unsatisfiable);

            var script = new TSqlEmitter().Emit(source, target, diff, closure.Selection);
            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script);

            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();

            Assert.Equal(
                ["dbo.Category", "dbo.Company", "dbo.Contact"],
                after.Tables.Select(t => t.Identity.QualifiedName).Order(StringComparer.Ordinal));

            // The point of the exercise: the key that would have failed is on the target and enforced.
            var contact = after.Tables.Single(t => t.Identity == Contact);
            Assert.Contains(contact.ForeignKeys, fk => fk.Name == "FK_Contact_Company");
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The failure closure exists to prevent, kept as a test so it stays a known behaviour rather than a
    // rediscovery. Emitting the literal tick produces a script that cannot run.
    [SkippableFact]
    public async Task Without_closure_the_same_tick_produces_a_script_that_rolls_back()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureOff";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            var script = new TSqlEmitter().Emit(source, target, diff, new HashSet<ObjectIdentity> { Contact });
            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script);

            Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            Assert.True(after.IsEmpty);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A view is not only ordered against what it reads, it fails outright if the table is missing —
    // which is why table dependencies had to join the ones the catalog query already returned.
    [SkippableFact]
    public async Task A_ticked_view_brings_the_table_it_selects_from()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureView";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            // sales.vActiveSegments reads sales.vCompanySegment, which reads dbo.Company, which needs
            // dbo.Category. Four objects out of one tick.
            var view = new ObjectIdentity(ObjectType.View, "sales", "vActiveSegments");
            var closure = SchemaClosure.Expand(source, target, diff, [view]);

            Assert.Equal(
                ["sales.vCompanySegment", "dbo.Company", "dbo.Category"],
                closure.Required.Select(r => r.Identity.QualifiedName));

            var script = new TSqlEmitter().Emit(source, target, diff, closure.Selection);
            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script);

            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            Assert.Contains(after.Views, v => v.Identity == view);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private static Task<DatabaseSchema> SourceAsync() =>
        new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
}
