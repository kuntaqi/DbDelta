using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// The reported repro, run for real: rename a view and a procedure on the source with sp_rename, then
// provision an empty target from it. sp_rename updates sys.objects.name and leaves sys.sql_modules alone,
// so emitting the stored text created vCategoryOld and usp_GetCategoryOld on the target — two objects the
// plan never asked for — and reported a successful apply.
//
// Its own source and its own target, because the source has to be mutated and the shared pair is read-only
// by contract.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionB))]
public sealed class RenamedProgrammableApplyTests
{
    private static readonly string[] Repro =
    [
        """
        CREATE TABLE dbo.Category (
            CategoryId  INT NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
            Name        NVARCHAR(50) NOT NULL
        );
        """,
        "CREATE PROCEDURE dbo.usp_GetCategoryOld AS SELECT CategoryId, Name FROM dbo.Category;",
        "CREATE VIEW dbo.vCategoryOld AS SELECT CategoryId, Name FROM dbo.Category;",
        "EXEC sp_rename 'dbo.usp_GetCategoryOld', 'usp_GetCategory';",
        "EXEC sp_rename 'dbo.vCategoryOld', 'vCategory';"
    ];

    private readonly LocalDbFixture _fixture;

    public RenamedProgrammableApplyTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_renamed_view_and_procedure_land_under_their_catalog_names_and_the_compare_converges()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string src = "RenamedSrc";
        const string tgt = "RenamedTgt";

        var sourceConnection = await _fixture.CreateEmptyTargetAsync(src);
        var targetConnection = await _fixture.CreateEmptyTargetAsync(tgt);

        try
        {
            foreach (var batch in Repro)
            {
                await ExecuteAsync(sourceConnection, batch);
            }

            var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();

            // The premise, asserted rather than assumed: the catalog and the stored text disagree. If a
            // future SQL Server ever fixed sp_rename, this is the line that would say so.
            var view = source.Views.Single();
            Assert.Equal("vCategory", view.Identity.Name);
            Assert.Contains("vCategoryOld", view.Definition, StringComparison.Ordinal);

            var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

            // The apply reports success either way, so the script has to be the thing that says the name
            // it ran is not the name the body asked for.
            Assert.Contains(
                script.InPhase(ScriptPhase.Programmables),
                s => s.Description.Contains("still creates it as dbo.vCategoryOld", StringComparison.Ordinal));

            var result = await new SqlServerScriptExecutor().ExecuteAsync(targetConnection, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(targetConnection).ReadAsync();

            Assert.Equal("vCategory", after.Views.Single().Identity.Name);
            Assert.Equal("usp_GetCategory", after.Routines.Single().Identity.Name);

            // What the defect produced: the plan asked for two objects and created two different ones.
            var names = after.Views.Select(v => v.Identity.Name)
                .Concat(after.Routines.Select(r => r.Identity.Name))
                .ToList();

            Assert.DoesNotContain(names, name => name.EndsWith("Old", StringComparison.Ordinal));

            // And the part that made it worse than a failed emit: a second compare has to be clean, or the
            // apply never settles no matter which name it wrote.
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(src);
            await _fixture.DropScratchAsync(tgt);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
