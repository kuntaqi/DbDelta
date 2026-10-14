using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// No database: the emitter turns a diff into phased DDL, so a real SchemaComparer over two hand-built schemas
// exercises it end to end without a server. The live apply-and-converge proof is in the integration test.
public sealed class PostgreSqlScriptEmitterTests
{
    private readonly SchemaComparer _comparer = new();
    private readonly PostgreSqlScriptEmitter _emitter = new();

    [Fact]
    public void A_source_only_table_emits_create_key_and_index_in_their_phases()
    {
        var table = new TableDefinition
        {
            Identity = new ObjectIdentity(ObjectType.Table, "app", "widget"),
            Columns =
            [
                new ColumnDefinition { Name = "id", DataType = new DataTypeSpec("integer"), OrdinalPosition = 1 },
                new ColumnDefinition { Name = "name", DataType = new DataTypeSpec("text"), IsNullable = true, OrdinalPosition = 2 },
            ],
            PrimaryKey = new PrimaryKeyDefinition { Name = "pk_widget", Columns = [new IndexColumn("id")] },
            Indexes = [new IndexDefinition { Name = "ix_widget_name", Columns = [new IndexColumn("name")] }],
        };

        var script = Emit(Schema("src", [table]), Schema("tgt"));

        var create = Assert.Single(script.InPhase(Core.Scripting.ScriptPhase.CreateTables));
        Assert.Contains("CREATE TABLE \"app\".\"widget\"", create.Sql, StringComparison.Ordinal);

        var key = Assert.Single(script.InPhase(Core.Scripting.ScriptPhase.Keys));
        Assert.Contains("ADD CONSTRAINT \"pk_widget\" PRIMARY KEY (\"id\")", key.Sql, StringComparison.Ordinal);

        var index = Assert.Single(script.InPhase(Core.Scripting.ScriptPhase.Indexes));
        Assert.Contains("CREATE INDEX \"ix_widget_name\" ON \"app\".\"widget\" USING btree (\"name\")", index.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_only_table_is_dropped_destructively()
    {
        var table = new TableDefinition { Identity = new ObjectIdentity(ObjectType.Table, "app", "legacy") };

        var script = Emit(Schema("src"), Schema("tgt", [table]));

        var drop = Assert.Single(script.InPhase(Core.Scripting.ScriptPhase.DropTables));
        Assert.Equal("DROP TABLE \"app\".\"legacy\";", drop.Sql);
        Assert.True(drop.Destructive);
    }

    [Fact]
    public void An_expression_index_is_refused_not_emitted()
    {
        var table = new TableDefinition
        {
            Identity = new ObjectIdentity(ObjectType.Table, "app", "widget"),
            Columns = [new ColumnDefinition { Name = "name", DataType = new DataTypeSpec("text"), IsNullable = true, OrdinalPosition = 1 }],
            Indexes = [new IndexDefinition { Name = "ix_lower", Columns = [new IndexColumn("lower(name)")] }],
        };

        var script = Emit(Schema("src", [table]), Schema("tgt"));

        Assert.Empty(script.InPhase(Core.Scripting.ScriptPhase.Indexes));
        Assert.Contains(script.Refusals, r => r.Contains("ix_lower", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_type_is_refused_because_there_is_no_safe_alter()
    {
        var source = new DatabaseSchema
        {
            DatabaseName = "src",
            UserDefinedTypes = [Enum("mood", "sad", "ok", "glad")],
        };
        var target = new DatabaseSchema
        {
            DatabaseName = "tgt",
            UserDefinedTypes = [Enum("mood", "sad", "ok", "happy")],
        };

        var script = _emitter.Emit(source, target, _comparer.Compare(source, target));

        Assert.True(script.IsEmpty);
        Assert.Contains(script.Refusals, r => r.Contains("mood", StringComparison.Ordinal));
    }

    [Fact]
    public void The_reviewable_script_uses_postgres_session_settings()
    {
        var table = new TableDefinition { Identity = new ObjectIdentity(ObjectType.Table, "app", "legacy") };
        var sql = Emit(Schema("src"), Schema("tgt", [table])).ToSql();

        Assert.DoesNotContain("XACT_ABORT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("QUOTED_IDENTIFIER", sql, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION;", sql, StringComparison.Ordinal);
    }

    private Core.Scripting.SyncScript Emit(DatabaseSchema source, DatabaseSchema target) =>
        _emitter.Emit(source, target, _comparer.Compare(source, target));

    private static DatabaseSchema Schema(string name, IReadOnlyList<TableDefinition>? tables = null) =>
        new() { DatabaseName = name, Tables = tables ?? [] };

    private static UserDefinedTypeDefinition Enum(string name, params string[] labels) =>
        new()
        {
            Identity = new ObjectIdentity(ObjectType.UserDefinedType, "app", name),
            Kind = UserDefinedTypeKind.Enum,
            EnumLabels = labels,
        };
}
