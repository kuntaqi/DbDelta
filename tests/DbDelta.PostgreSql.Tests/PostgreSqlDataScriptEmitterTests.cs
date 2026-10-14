using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// No database: the data emitter turns row changes into DML text, so the statement shape and the inline-vs-staged
// decision are pinned here. The live apply-and-converge proof is in PostgreSqlDataSyncPathTests.
public sealed class PostgreSqlDataScriptEmitterTests
{
    private readonly PostgreSqlDataScriptEmitter _emitter = new();

    [Fact]
    public void An_insert_is_a_typed_values_statement()
    {
        var changes = Changes(Insert(id: "1", name: "a"));
        var step = Assert.Single(_emitter.Emit(changes, long.MaxValue));

        Assert.Equal(ScriptPhase.DataUpserts, step.Phase);
        Assert.Contains("INSERT INTO \"app\".\"widget\" (\"id\", \"name\")", step.Sql, StringComparison.Ordinal);
        Assert.Contains("('1'::integer, 'a'::text)", step.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_sets_non_key_columns_by_key()
    {
        var changes = Changes(Update(id: "1", name: "z"));
        var step = Assert.Single(_emitter.Emit(changes, long.MaxValue));

        Assert.Equal("UPDATE \"app\".\"widget\" SET \"name\" = 'z'::text WHERE \"id\" = '1'::integer;", step.Sql);
    }

    [Fact]
    public void A_delete_is_destructive_and_keyed()
    {
        var changes = Changes(Delete(id: "9"));
        var step = Assert.Single(_emitter.Emit(changes, long.MaxValue));

        Assert.Equal(ScriptPhase.DataDeletes, step.Phase);
        Assert.True(step.Destructive);
        Assert.Equal("DELETE FROM \"app\".\"widget\" WHERE \"id\" = '9'::integer;", step.Sql);
    }

    [Fact]
    public void Past_the_inline_limit_rows_go_through_a_staging_table()
    {
        var changes = Changes(Insert(id: "1", name: "a"), Update(id: "2", name: "b"));
        var steps = _emitter.Emit(changes, 0);

        Assert.Contains(steps, s => s.Sql.Contains("CREATE TEMP TABLE", StringComparison.Ordinal));
        var load = Assert.Single(steps, s => s.Load is not null);
        Assert.Equal(2, load.Load!.RowCount);
        Assert.Contains(steps, s => s.Sql.Contains("INSERT INTO \"app\".\"widget\"", StringComparison.Ordinal)
            && s.Sql.Contains("WHERE \"_op\" = 'I'", StringComparison.Ordinal));
    }

    [Fact]
    public void Inserting_an_identity_table_reseeds_its_sequence()
    {
        var changes = Changes(identity: true, Insert(id: "5", name: "a"));
        var steps = _emitter.Emit(changes, long.MaxValue);

        Assert.Contains(steps, s => s.Sql.Contains("setval(pg_get_serial_sequence(", StringComparison.Ordinal));
    }

    private static TableDataChanges Changes(params DataChange[] changes) => Changes(false, changes);

    private static TableDataChanges Changes(bool identity, params DataChange[] changes) => new()
    {
        Table = new TableDefinition
        {
            Identity = new ObjectIdentity(ObjectType.Table, "app", "widget"),
            Columns =
            [
                new ColumnDefinition
                {
                    Name = "id", DataType = new DataTypeSpec("integer"), OrdinalPosition = 1,
                    Identity = identity ? new IdentitySpec(1, 1) : null,
                },
                new ColumnDefinition { Name = "name", DataType = new DataTypeSpec("text"), IsNullable = true, OrdinalPosition = 2 },
            ],
        },
        KeyColumns = ["id"],
        Columns = ["name"],
        Changes = changes,
        TargetRowCount = 10,
    };

    private static DataChange Insert(string id, string name) =>
        new(id, id, RowClassification.Insert, Values(id, name), Key(id));

    private static DataChange Update(string id, string name) =>
        new(id, id, RowClassification.Update, Values(id, name), Key(id));

    private static DataChange Delete(string id) =>
        new(id, id, RowClassification.Delete, new Dictionary<string, string?>(), Key(id));

    private static Dictionary<string, string?> Values(string id, string name) =>
        new(StringComparer.OrdinalIgnoreCase) { ["id"] = id, ["name"] = name };

    private static Dictionary<string, string?> Key(string id) =>
        new(StringComparer.OrdinalIgnoreCase) { ["id"] = id };
}
