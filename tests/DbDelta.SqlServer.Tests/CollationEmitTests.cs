using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// Self-contained: its own source and its own target, both created here, because the point is a pair whose
// database defaults differ and the shared fixture cannot have that — everything on one instance is created
// with the instance's default unless something says otherwise.
//
// That is exactly why this went unnoticed. A source and a target that share a default produce identical
// columns whether or not the emitter writes COLLATE, so nothing that compared the two could tell.
[Collection(nameof(LocalDbCollection))]
public sealed class CollationEmitTests
{
    // Not the instance default, so a column that takes the target's default is visibly wrong.
    private const string TargetDefault = "Latin1_General_CI_AI";
    private const string SourceDefault = "SQL_Latin1_General_CP1_CI_AS";
    private const string Explicit = "Latin1_General_CS_AS";

    private const string Ddl = $"""
        CREATE TABLE dbo.Doc (
            DocId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Doc PRIMARY KEY,
            Title    VARCHAR(50) NOT NULL,
            Code     VARCHAR(20) COLLATE {Explicit} NOT NULL,
            Body     NVARCHAR(MAX) NULL,
            Total    DECIMAL(18,2) NOT NULL
        );
        """;

    private readonly LocalDbFixture _fixture;

    public CollationEmitTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_table_created_in_a_differently_collated_database_keeps_the_source_collations()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string src = "CollEmitSrc";
        const string tgt = "CollEmitTgt";

        var sourceConnection = await _fixture.CreateEmptyTargetAsync(src, SourceDefault);
        var targetConnection = await _fixture.CreateEmptyTargetAsync(tgt, TargetDefault);

        try
        {
            await ExecuteAsync(sourceConnection, Ddl);

            var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();
            var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

            var result = await new SqlServerScriptExecutor().ExecuteAsync(targetConnection, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(targetConnection).ReadAsync();
            var doc = after.Tables.Single(t => t.Identity.Name == "Doc");

            // The column with no COLLATE of its own: it must carry the source database's default, not the
            // target's. This is the one that was wrong before, and silently — the table exists, the types
            // match, and only the collation moved.
            Assert.Equal(SourceDefault, Column(doc, "Title").Collation);

            // And the one that overrides it keeps its override.
            Assert.Equal(Explicit, Column(doc, "Code").Collation);

            // A replica compares clean against what it was built from. Nothing else in this test would
            // catch a column this tool creates and then reports as different from its own source.
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(src);
            await _fixture.DropScratchAsync(tgt);
        }
    }

    // The other half of the rule. Writing COLLATE on every string column would also be correct, and it is
    // not what this does: a column that would get what it needs from the database it is created in is left
    // alone, so the script stays readable.
    [SkippableFact]
    public async Task No_collate_is_written_where_the_target_default_already_gives_the_right_answer()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string src = "CollSameSrc";
        const string tgt = "CollSameTgt";

        var sourceConnection = await _fixture.CreateEmptyTargetAsync(src, SourceDefault);
        var targetConnection = await _fixture.CreateEmptyTargetAsync(tgt, SourceDefault);

        try
        {
            await ExecuteAsync(sourceConnection, Ddl);

            var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();
            var sql = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target)).ToSql();

            // Both sides agree on the default, so only the column that overrides it needs saying.
            Assert.Contains($"COLLATE {Explicit}", sql, StringComparison.Ordinal);
            Assert.DoesNotContain($"COLLATE {SourceDefault}", sql, StringComparison.Ordinal);

            // A non-textual column has no collation to write, whatever the defaults are doing.
            Assert.DoesNotContain("[Total] DECIMAL(18,2) COLLATE", sql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await _fixture.DropScratchAsync(src);
            await _fixture.DropScratchAsync(tgt);
        }
    }

    // Changing a column's collation is what unblocks a table the data-compare precondition refused, so it
    // has to be something the emitter can actually express. ALTER COLUMN with no COLLATE resets the column
    // to the database default rather than keeping what it had, which is why this is not a no-op statement.
    [SkippableFact]
    public async Task A_column_whose_collation_differs_is_altered_to_the_source_collation()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string src = "CollAlterSrc";
        const string tgt = "CollAlterTgt";

        var sourceConnection = await _fixture.CreateEmptyTargetAsync(src, SourceDefault);
        var targetConnection = await _fixture.CreateEmptyTargetAsync(tgt, SourceDefault);

        try
        {
            await ExecuteAsync(sourceConnection, Ddl);

            // The same table, but Code collated the way the target happens to have it.
            await ExecuteAsync(targetConnection, Ddl.Replace($"COLLATE {Explicit} ", string.Empty, StringComparison.Ordinal));

            var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);
            var script = new TSqlEmitter().Emit(source, target, diff);

            Assert.Contains($"ALTER COLUMN [Code] VARCHAR(20) COLLATE {Explicit}", script.ToSql(), StringComparison.Ordinal);

            var result = await new SqlServerScriptExecutor().ExecuteAsync(targetConnection, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(targetConnection).ReadAsync();

            Assert.Equal(Explicit, Column(after.Tables.Single(t => t.Identity.Name == "Doc"), "Code").Collation);
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(src);
            await _fixture.DropScratchAsync(tgt);
        }
    }

    private static ColumnDefinition Column(TableDefinition table, string name) =>
        table.Columns.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
