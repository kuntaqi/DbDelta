using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer.Tests;

// What each kind of column difference turns into, decided from schemas in memory. ALTER COLUMN changes a
// type, a nullability or a collation and nothing else, and the server refuses it while anything still
// holds the column. Both halves of that used to be ignored: identity was written into the ALTER (error
// 156, and the whole script refused to compile), and a default on the column was left where it stood
// (error 4922, and the transaction rolled back). ColumnDependencyApplyTests proves the scripts run; this
// pins the shape without paying for a server.
public sealed class ColumnChangeEmitTests
{
    private static readonly ObjectIdentity Category = new(ObjectType.Table, "dbo", "Category");
    private static readonly ObjectIdentity Item = new(ObjectType.Table, "dbo", "Item");

    private static readonly DataTypeSpec Int = SqlTypeMapper.Map("int", 4, 10, 0);
    private static readonly DataTypeSpec BigInt = SqlTypeMapper.Map("bigint", 8, 19, 0);
    private static readonly DataTypeSpec Datetime = SqlTypeMapper.Map("datetime", 8, 23, 3);
    private static readonly DataTypeSpec Datetime2 = SqlTypeMapper.Map("datetime2", 8, 27, 7);

    private static ColumnDefinition Col(string name, DataTypeSpec type, int ordinal, bool nullable = true) =>
        new() { Name = name, DataType = type, IsNullable = nullable, OrdinalPosition = ordinal };

    private static TableDefinition Table(params ColumnDefinition[] columns) =>
        new() { Identity = Category, Columns = columns };

    // The other tables are the same on both sides, so only the one under test differs.
    private static SyncScript Emit(TableDefinition source, TableDefinition target, params TableDefinition[] unchanged) =>
        Emit(new DatabaseSchema { DatabaseName = "Src", Tables = [source, .. unchanged] },
            new DatabaseSchema { DatabaseName = "Tgt", Tables = [target, .. unchanged] });

    private static SyncScript Emit(DatabaseSchema source, DatabaseSchema target) =>
        new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

    private static int IndexOf(SyncScript script, string fragment)
    {
        var index = script.Steps.ToList().FindIndex(s => s.Sql.Contains(fragment, StringComparison.Ordinal));
        Assert.True(index >= 0, $"no step contains {fragment}");
        return index;
    }

    [Fact]
    public void No_alter_column_ever_carries_an_identity_clause()
    {
        // The same identity on both sides, so the column is altered in place: int to bigint keeps its
        // identity property without being told to, and saying so is a syntax error.
        var source = Table(new ColumnDefinition { Name = "CategoryId", DataType = BigInt, OrdinalPosition = 1, Identity = new IdentitySpec(1, 1) });
        var target = Table(new ColumnDefinition { Name = "CategoryId", DataType = Int, OrdinalPosition = 1, Identity = new IdentitySpec(1, 1) });

        var script = Emit(source, target);

        var alter = Assert.Single(script.Steps, s => s.Sql.Contains("ALTER COLUMN", StringComparison.Ordinal));
        Assert.Equal("ALTER TABLE [dbo].[Category] ALTER COLUMN [CategoryId] BIGINT NOT NULL;", alter.Sql);
        Assert.Empty(script.Refusals);
    }

    [Fact]
    public void An_identity_difference_rebuilds_the_table()
    {
        var source = Table(
            new ColumnDefinition { Name = "CategoryId", DataType = Int, OrdinalPosition = 1, Identity = new IdentitySpec(1, 2) },
            Col("Name", SqlTypeMapper.Map("varchar", 50, 0, 0), 2));
        var target = Table(
            Col("CategoryId", Int, 1, nullable: false),
            Col("Name", SqlTypeMapper.Map("varchar", 50, 0, 0), 2));

        var script = Emit(source, target);
        var sql = script.ToSql();

        Assert.DoesNotContain("ALTER COLUMN", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [dbo].[Category_dbdelta_rebuild]", sql, StringComparison.Ordinal);
        Assert.Contains("[CategoryId] INT IDENTITY(1,2) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Category_dbdelta_rebuild] ON;", sql, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE [dbo].[Category];", sql, StringComparison.Ordinal);
        Assert.Contains("sp_rename N''[dbo].[Category_dbdelta_rebuild]'', N''Category''", sql, StringComparison.Ordinal);
        Assert.Contains("IDENTITY(1,2) on the source and not an identity column on the target", sql, StringComparison.Ordinal);

        // Dropping the original is the step that cannot be taken back, so it is the one that says so.
        Assert.Contains(script.Steps, s => s.Destructive && s.Sql.Contains("DROP TABLE [dbo].[Category];", StringComparison.Ordinal));
        Assert.True(IndexOf(script, "INSERT INTO [dbo].[Category_dbdelta_rebuild]") < IndexOf(script, "DROP TABLE [dbo].[Category];"));
    }

    [Fact]
    public void A_schema_bound_module_refuses_the_rebuild_and_the_rest_still_goes_ahead()
    {
        var source = Table(
            new ColumnDefinition { Name = "CategoryId", DataType = BigInt, OrdinalPosition = 1, Identity = new IdentitySpec(1, 1) },
            Col("ReviewedOn", Datetime, 2));
        var target = new TableDefinition
        {
            Identity = Category,
            Columns = [Col("CategoryId", Int, 1, nullable: false), Col("ReviewedOn", Datetime2, 2)],
            SchemaBoundReferences = [new SchemaBoundReference(new ObjectIdentity(ObjectType.View, "dbo", "vCategoryCount"), [])]
        };

        var script = Emit(source, target);

        var refusal = Assert.Single(script.Refusals);
        Assert.Contains("dbo.Category.CategoryId", refusal, StringComparison.Ordinal);
        Assert.Contains("dbo.vCategoryCount is schema-bound to it", refusal, StringComparison.Ordinal);

        // The type half of the identity column's difference can still be made; the identity half cannot.
        Assert.Contains(script.Steps, s => s.Sql == "ALTER TABLE [dbo].[Category] ALTER COLUMN [CategoryId] BIGINT NOT NULL;");
        Assert.Contains(script.Steps, s => s.Sql == "ALTER TABLE [dbo].[Category] ALTER COLUMN [ReviewedOn] DATETIME NULL;");
        Assert.DoesNotContain(script.Steps, s => s.Sql.Contains("IDENTITY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_default_comes_down_before_its_column_is_altered_and_goes_back_after()
    {
        ColumnDefinition LastUpdated(DataTypeSpec type) => new()
        {
            Name = "LastUpdated",
            DataType = type,
            IsNullable = true,
            OrdinalPosition = 2,
            DefaultExpression = "(getdate())",
            DefaultConstraintName = "DF_Category_LastUpdated"
        };

        var script = Emit(
            Table(Col("CategoryId", Int, 1, false), LastUpdated(Datetime)),
            Table(Col("CategoryId", Int, 1, false), LastUpdated(Datetime2)));

        var drop = IndexOf(script, "DROP CONSTRAINT [DF_Category_LastUpdated]");
        var alter = IndexOf(script, "ALTER COLUMN [LastUpdated] DATETIME NULL");
        var add = IndexOf(script, "ADD CONSTRAINT [DF_Category_LastUpdated] DEFAULT (getdate()) FOR [LastUpdated]");

        Assert.True(drop < alter && alter < add, $"drop {drop}, alter {alter}, add {add}");
    }

    [Fact]
    public void A_default_only_difference_moves_the_default_and_alters_nothing()
    {
        ColumnDefinition Status(string? expression) => new()
        {
            Name = "Status",
            DataType = Int,
            IsNullable = true,
            OrdinalPosition = 2,
            DefaultExpression = expression,
            DefaultConstraintName = expression is null ? null : "DF_Category_Status"
        };

        var script = Emit(
            Table(Col("CategoryId", Int, 1, false), Status("((1))")),
            Table(Col("CategoryId", Int, 1, false), Status("((0))")));

        Assert.DoesNotContain(script.Steps, s => s.Sql.Contains("ALTER COLUMN", StringComparison.Ordinal));
        Assert.True(IndexOf(script, "DROP CONSTRAINT [DF_Category_Status]") < IndexOf(script, "DEFAULT ((1)) FOR [Status]"));
    }

    [Fact]
    public void Everything_that_holds_a_column_is_bracketed_around_its_alter()
    {
        var source = new TableDefinition
        {
            Identity = Category,
            Columns =
            [
                Col("CategoryId", BigInt, 1, false),
                Col("Score", BigInt, 2),
                new ColumnDefinition
                {
                    Name = "ScoreBand", DataType = BigInt, OrdinalPosition = 3, IsNullable = true,
                    ComputedExpression = "([Score]/(10))", ComputedFrom = ["Score"]
                }
            ],
            PrimaryKey = new PrimaryKeyDefinition { Name = "PK_Category", Columns = [new IndexColumn("CategoryId", false)] },
            CheckConstraints = [new CheckConstraintDefinition { Name = "CK_Category_Score", Expression = "([Score]>=(0))", Columns = ["Score"] }],
            Indexes = [new IndexDefinition { Name = "IX_Category_Positive", Columns = [new IndexColumn("CategoryId", false)], FilterExpression = "([Score]>(0))" }],
            Statistics = []
        };

        var target = new TableDefinition
        {
            Identity = Category,
            Columns =
            [
                Col("CategoryId", Int, 1, false),
                Col("Score", Int, 2),
                new ColumnDefinition
                {
                    Name = "ScoreBand", DataType = Int, OrdinalPosition = 3, IsNullable = true,
                    ComputedExpression = "([Score]/(10))", ComputedFrom = ["Score"]
                }
            ],
            PrimaryKey = source.PrimaryKey,
            // No catalog column list here, as when the dependency query was refused: the bracketed
            // reference in the stored expression is what finds it.
            CheckConstraints = [new CheckConstraintDefinition { Name = "CK_Category_Score", Expression = "([Score]>=(0))" }],
            Indexes = source.Indexes,
            Statistics = [new StatisticsDefinition { Name = "ST_Category_Score", Columns = ["Score"] }]
        };

        var item = new TableDefinition
        {
            Identity = Item,
            Columns = [Col("ItemId", Int, 1, false), Col("CategoryId", Int, 2, false)],
            ForeignKeys = [new ForeignKeyDefinition { Name = "FK_Item_Category", Columns = ["CategoryId"], ReferencedTable = Category, ReferencedColumns = ["CategoryId"] }]
        };

        var script = Emit(source, target, item);
        var alterKey = IndexOf(script, "ALTER COLUMN [CategoryId] BIGINT NOT NULL");
        var alterScore = IndexOf(script, "ALTER COLUMN [Score] BIGINT NULL");

        foreach (var drop in new[]
        {
            "DROP CONSTRAINT [FK_Item_Category]",
            "DROP CONSTRAINT [PK_Category]",
            "DROP CONSTRAINT [CK_Category_Score]",
            "DROP INDEX [IX_Category_Positive]",
            "DROP STATISTICS [dbo].[Category].[ST_Category_Score]",
            "DROP COLUMN [ScoreBand]"
        })
        {
            Assert.True(IndexOf(script, drop) < Math.Min(alterKey, alterScore), $"{drop} must come before the alters");
        }

        foreach (var restore in new[]
        {
            "ADD CONSTRAINT [FK_Item_Category] FOREIGN KEY",
            "ADD CONSTRAINT [PK_Category] PRIMARY KEY",
            "ADD CONSTRAINT [CK_Category_Score] CHECK",
            "CREATE INDEX [IX_Category_Positive]",
            "CREATE STATISTICS [ST_Category_Score]",
            "ADD [ScoreBand] AS ([Score]/(10))"
        })
        {
            Assert.True(IndexOf(script, restore) > Math.Max(alterKey, alterScore), $"{restore} must come after the alters");
        }

        // The foreign key lives on another table, so it goes back as the target had it, and exactly once.
        Assert.Single(script.Steps, s => s.Sql.Contains("DROP CONSTRAINT [FK_Item_Category]", StringComparison.Ordinal));
        Assert.Single(script.Steps, s => s.Sql.Contains("ADD CONSTRAINT [FK_Item_Category]", StringComparison.Ordinal));
        Assert.Empty(script.Refusals);
    }

    [Fact]
    public void A_column_a_schema_bound_view_reads_is_refused_rather_than_altered()
    {
        var source = Table(Col("CategoryId", Int, 1, false), Col("Name", SqlTypeMapper.Map("varchar", 80, 0, 0), 2));
        var target = new TableDefinition
        {
            Identity = Category,
            Columns = [Col("CategoryId", Int, 1, false), Col("Name", SqlTypeMapper.Map("varchar", 50, 0, 0), 2)],
            SchemaBoundReferences = [new SchemaBoundReference(new ObjectIdentity(ObjectType.View, "dbo", "vCategoryName"), ["Name"])]
        };

        var script = Emit(source, target);

        Assert.DoesNotContain(script.Steps, s => s.Sql.Contains("ALTER COLUMN", StringComparison.Ordinal));
        var refusal = Assert.Single(script.Refusals);
        Assert.Contains("dbo.Category.Name cannot be altered while dbo.vCategoryName is schema-bound to it", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropping_a_column_takes_its_default_down_first()
    {
        var source = Table(Col("CategoryId", Int, 1, false));
        var target = Table(
            Col("CategoryId", Int, 1, false),
            new ColumnDefinition
            {
                Name = "Legacy", DataType = Int, IsNullable = true, OrdinalPosition = 2,
                DefaultExpression = "((0))", DefaultConstraintName = "DF_Category_Legacy"
            });

        var script = Emit(source, target);

        Assert.True(IndexOf(script, "DROP CONSTRAINT [DF_Category_Legacy]") < IndexOf(script, "DROP COLUMN [Legacy]"));
        Assert.DoesNotContain(script.Steps, s => s.Sql.Contains("ADD CONSTRAINT [DF_Category_Legacy]", StringComparison.Ordinal));
    }

    [Fact]
    public void Column_order_alone_is_reported_rather_than_emitted_as_an_alter_that_changes_nothing()
    {
        var source = new DatabaseSchema { DatabaseName = "Src", Tables = [Table(Col("A", Int, 1), Col("B", Int, 2))] };
        var target = new DatabaseSchema { DatabaseName = "Tgt", Tables = [Table(Col("A", Int, 2), Col("B", Int, 1))] };
        var comparer = new SchemaComparer(new ComparisonOptions { IgnoreColumnOrder = false });

        var script = new TSqlEmitter().Emit(source, target, comparer.Compare(source, target));

        Assert.Empty(script.Steps);
        Assert.Equal(2, script.Refusals.Count);
        Assert.All(script.Refusals, r => Assert.Contains("different position", r, StringComparison.Ordinal));
    }
}
