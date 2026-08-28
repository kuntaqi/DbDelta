using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// Parent closure is only worth anything if the rows it adds make the script run. These go to the server:
// seed a child, let closure walk up, execute, then read the target back.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionD))]
public sealed class ParentClosureIntegrationTests
{
    private readonly LocalDbFixture _fixture;

    public ParentClosureIntegrationTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Seeding_a_child_pulls_the_parent_rows_it_references_and_the_script_commits()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureRows";
        var connectionString = await _fixture.CreateChildTargetAsync(scratch);

        try
        {
            var (source, target) = await SchemasAsync(connectionString);

            // Only dbo.Contact is picked. Its one row sits on dbo.Company 2, which sits in dbo.Category 3,
            // and the target has neither.
            var planned = await ChangesAsync(source, target, connectionString, "Contact");

            var closure = await ExpandAsync(source, target, connectionString, planned);

            Assert.Equal(
                [("dbo.Company", 1), ("dbo.Category", 1)],
                closure.Added.Select(a => (a.Table.QualifiedName, a.RowCount)));
            Assert.Empty(closure.Warnings);

            var result = await ExecuteAsync(source, closure, connectionString);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM dbo.Contact"));
            Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM dbo.Company WHERE CompanyId = 2"));
            Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM dbo.Category WHERE CategoryId = 3"));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The failure closure exists to prevent, kept as a test so it stays a known behaviour rather than a
    // rediscovery. The rows go in exactly as picked and the foreign key refuses them.
    [SkippableFact]
    public async Task Without_closure_the_same_seed_rolls_back_on_the_foreign_key()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureRowsOff";
        var connectionString = await _fixture.CreateChildTargetAsync(scratch);

        try
        {
            var (source, _) = await SchemasAsync(connectionString);
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var planned = await ChangesAsync(source, target, connectionString, "Contact");

            var script = Assemble(source, planned);
            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script);

            Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
            Assert.Contains("FK_Contact_Company", result.ServerMessage);
            Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM dbo.Contact"));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A parent row already on the target is not fetched again: closure adds what is missing, not what is
    // referenced. Northwind sits in category 1, which the target has.
    [SkippableFact]
    public async Task A_parent_row_the_target_already_has_is_left_alone()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureHave";
        var connectionString = await _fixture.CreateChildTargetAsync(scratch);

        try
        {
            var (source, target) = await SchemasAsync(connectionString);
            var planned = await ChangesAsync(source, target, connectionString, "Company");

            var closure = await ExpandAsync(source, target, connectionString, planned);

            // Both companies go in, but only category 3 is missing — category 1 is already there.
            var added = Assert.Single(closure.Added);
            Assert.Equal("dbo.Category", added.Table.QualifiedName);
            Assert.Equal(1, added.RowCount);

            var category = closure.Tables.Single(t => t.Table.Identity.Name == "Category");
            Assert.Equal(["3"], category.Changes.Select(c => c.Display));

            var result = await ExecuteAsync(source, closure, connectionString);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // Picking the parent as well must not write its rows twice: closure has to see what the plan already
    // holds, not only what the target does.
    [SkippableFact]
    public async Task A_parent_already_in_the_plan_is_not_added_a_second_time()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ClosureBoth";
        var connectionString = await _fixture.CreateChildTargetAsync(scratch);

        try
        {
            var (source, target) = await SchemasAsync(connectionString);

            var planned = new List<TableDataChanges>();
            foreach (var name in new[] { "Category", "Company", "Contact" })
            {
                planned.AddRange(await ChangesAsync(source, target, connectionString, name));
            }

            var closure = await ExpandAsync(source, target, connectionString, planned);

            Assert.Empty(closure.Added);

            var category = closure.Tables.Single(t => t.Table.Identity.Name == "Category");
            Assert.Equal(
                category.Changes.Select(c => c.Key).Distinct().Count(),
                category.Changes.Count);

            var result = await ExecuteAsync(source, closure, connectionString);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private static async Task<(DatabaseSchema Source, DatabaseSchema Target)> SchemasAsync(string target) =>
        (await new SqlServerSchemaReader(
                LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync(),
         await new SqlServerSchemaReader(target).ReadAsync());

    private static Task<ParentClosureResult> ExpandAsync(
        DatabaseSchema source,
        DatabaseSchema target,
        string targetConnection,
        IReadOnlyList<TableDataChanges> planned) =>
        new ParentClosure(
                new SqlServerRowByValueReader(LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)),
                new SqlServerRowByValueReader(targetConnection),
                5000)
            .ExpandAsync(source, target, planned, ColumnSetResolver.DefaultKeyFor);

    // The same two-pass compare the API runs for a picked table, reduced to what these tests need.
    private static async Task<List<TableDataChanges>> ChangesAsync(
        DatabaseSchema source,
        DatabaseSchema target,
        string targetConnection,
        string tableName)
    {
        var sourceTable = source.Tables.Single(t => t.Identity.Name == tableName);
        var targetTable = target.Tables.Single(t => t.Identity.Name == tableName);
        var sourceConnection = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);

        var request = new DataCompareRequest
        {
            Table = sourceTable.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(sourceTable)
        };

        var columns = ColumnSetResolver.Resolve(sourceTable, targetTable, request);

        var result = await DataComparer.CompareAsync(
            new SqlServerRowHashReader(sourceConnection)
                .StreamAsync(sourceTable, request, columns.ComparedColumns, RowSetSide.Source),
            new SqlServerRowHashReader(targetConnection)
                .StreamAsync(targetTable, request, columns.ComparedColumns, RowSetSide.Target),
            new DataCompareSettings { Mode = TableDataMode.AllRows, ComparedColumns = columns.ComparedColumns });

        if (!result.HasChanges)
        {
            return [];
        }

        var fetched = columns.ComparedColumns.Concat(request.KeyColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var rows = (await new SqlServerRowDetailReader(sourceConnection).FetchAsync(
                sourceTable, request, fetched,
                result.Differences.Where(d => d.Classification != RowClassification.Delete)
                    .Select(d => d.Key).ToList()))
            .ToDictionary(r => r.Key, StringComparer.Ordinal);

        var changes = result.Differences
            .Where(d => rows.ContainsKey(d.Key))
            .Select(d =>
            {
                var row = rows[d.Key];
                var keys = request.KeyColumns.ToDictionary(
                    c => c, c => row.Values.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase);

                return new DataChange(
                    d.Key,
                    string.Join(", ", keys.Values.Select(v => v ?? "NULL")),
                    d.Classification,
                    row.Values,
                    keys);
            })
            .ToList();

        return
        [
            new TableDataChanges
            {
                Table = sourceTable,
                KeyColumns = request.KeyColumns,
                Columns = columns.ComparedColumns,
                Changes = changes
            }
        ];
    }

    private static Task<ApplyResult> ExecuteAsync(
        DatabaseSchema source,
        ParentClosureResult closure,
        string connectionString)
    {
        var script = Assemble(source, closure.Tables);

        return new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
    }

    // Parents first, which is what makes the pulled-in rows land before the rows that need them.
    private static SyncScript Assemble(DatabaseSchema source, IReadOnlyList<TableDataChanges> tables)
    {
        var order = TableDependencyGraph.Build(source.Tables).OrderForData();
        var ranked = order.Ordered.Concat(order.Cyclic).ToList();
        var byTable = tables.ToDictionary(t => t.Table.Identity);
        var steps = new List<ScriptStep>();

        foreach (var table in ranked.Where(byTable.ContainsKey))
        {
            steps.AddRange(new TSqlDataEmitter().Emit(byTable[table]));
        }

        return new SyncScript { Header = "-- parent closure", Steps = steps };
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
