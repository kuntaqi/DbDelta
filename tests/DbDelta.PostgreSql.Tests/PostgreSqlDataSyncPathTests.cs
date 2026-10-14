using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// The data-sync write path's real proof, tying the readers, emitter and executor together: compare two tables,
// turn the differences into a data script, apply it to the target, then compare again — a correct sync makes the
// second comparison empty. Run both ways: inline literals (maxInlineBytes huge) and the staged COPY path
// (maxInlineBytes 0), so the executor's COPY branch is exercised too.
[Collection(nameof(PgCollection))]
[Trait("Speed", "Slow")]
[Trait("RequiresPostgres", "true")]
public sealed class PostgreSqlDataSyncPathTests
{
    private const string Ddl = """
        drop schema if exists ds_src cascade;
        drop schema if exists ds_tgt cascade;
        create schema ds_src;
        create schema ds_tgt;
        create table ds_src.item (id integer primary key, name text, qty integer);
        create table ds_tgt.item (id integer primary key, name text, qty integer);
        insert into ds_src.item values (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);
        insert into ds_tgt.item values (2, 'b', 20), (3, 'c', 99), (4, 'd', 40);
        """;

    private static readonly string[] Compared = ["name", "qty"];
    private static readonly string[] AllColumns = ["id", "name", "qty"];

    private readonly PgFixture _fixture;

    public PostgreSqlDataSyncPathTests(PgFixture fixture) => _fixture = fixture;

    [SkippableTheory]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    public async Task Syncs_table_data_and_converges(long maxInlineBytes)
    {
        Skip.IfNot(_fixture.Available, $"PostgreSQL is not available: {_fixture.UnavailableReason}");

        await _fixture.ExecuteAsync(Ddl);
        var conn = _fixture.ConnectionString!;

        var source = Item("ds_src");
        var target = Item("ds_tgt");
        var request = new DataCompareRequest { Table = source.Identity, KeyColumns = ["id"], Mode = TableDataMode.AllRows };
        var settings = new DataCompareSettings { Mode = TableDataMode.AllRows, ComparedColumns = Compared };

        var before = await CompareAsync(conn, source, target, request, settings);
        Assert.True(before.HasChanges);

        var changes = await BuildChangesAsync(conn, source, target, request, before);
        var steps = new PostgreSqlDataScriptEmitter().Emit(changes, maxInlineBytes);
        var script = new SyncScript { Header = "-- data sync", Steps = steps.OrderBy(s => s.Phase).ToList() };

        var apply = await new PostgreSqlScriptExecutor().ExecuteAsync(conn, script);
        Assert.True(apply.Succeeded, apply.ServerMessage ?? apply.Message);

        var after = await CompareAsync(conn, source, target, request, settings);
        Assert.False(after.HasChanges);

        await _fixture.ExecuteAsync("drop schema if exists ds_src cascade; drop schema if exists ds_tgt cascade;");
    }

    private static async Task<DataCompareResult> CompareAsync(
        string conn, TableDefinition source, TableDefinition target, DataCompareRequest request, DataCompareSettings settings)
    {
        var reader = new PostgreSqlRowHashReader(conn);
        return await DataComparer.CompareAsync(
            reader.StreamAsync(source, request, Compared, RowSetSide.Source),
            reader.StreamAsync(target, request, Compared, RowSetSide.Target),
            settings);
    }

    // Stands in for DataCompareService: for inserts and updates the source values are fetched, for deletes the
    // target's own key values; each row difference becomes a DataChange the emitter understands.
    private static async Task<TableDataChanges> BuildChangesAsync(
        string conn, TableDefinition source, TableDefinition target, DataCompareRequest request, DataCompareResult result)
    {
        var detail = new PostgreSqlRowDetailReader(conn);

        var upsertKeys = result.Of(RowClassification.Insert).Concat(result.Of(RowClassification.Update))
            .Select(d => d.Key).ToList();
        var sourceRows = (await detail.FetchAsync(source, request, AllColumns, upsertKeys)).ToDictionary(r => r.Key);

        var deleteKeys = result.Of(RowClassification.Delete).Select(d => d.Key).ToList();
        var targetRows = (await detail.FetchAsync(target, request, AllColumns, deleteKeys)).ToDictionary(r => r.Key);

        var changes = new List<DataChange>();
        foreach (var difference in result.Differences)
        {
            if (difference.Classification == RowClassification.Delete)
            {
                var values = targetRows[difference.Key].Values;
                changes.Add(new DataChange(
                    difference.Key, difference.Key, RowClassification.Delete,
                    new Dictionary<string, string?>(), KeyOf(values)));
            }
            else
            {
                var values = sourceRows[difference.Key].Values;
                changes.Add(new DataChange(
                    difference.Key, difference.Key, difference.Classification, values, KeyOf(values)));
            }
        }

        return new TableDataChanges
        {
            Table = target,
            KeyColumns = ["id"],
            Columns = Compared,
            Changes = changes,
            TargetRowCount = 3,
        };
    }

    private static Dictionary<string, string?> KeyOf(IReadOnlyDictionary<string, string?> values) =>
        new(StringComparer.OrdinalIgnoreCase) { ["id"] = values["id"] };

    private static TableDefinition Item(string schema) => new()
    {
        Identity = new ObjectIdentity(ObjectType.Table, schema, "item"),
        Columns =
        [
            new ColumnDefinition { Name = "id", DataType = new DataTypeSpec("integer"), OrdinalPosition = 1 },
            new ColumnDefinition { Name = "name", DataType = new DataTypeSpec("text"), IsNullable = true, OrdinalPosition = 2 },
            new ColumnDefinition { Name = "qty", DataType = new DataTypeSpec("integer"), IsNullable = true, OrdinalPosition = 3 },
        ],
        PrimaryKey = new PrimaryKeyDefinition { Name = "item_pkey", Columns = [new IndexColumn("id")] },
    };
}
