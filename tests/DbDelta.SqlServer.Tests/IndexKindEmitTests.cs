using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer.Tests;

// Which statement each index kind produces, and what happens to a kind that has no statement this tool can
// write. Schemas in memory, no LocalDB: the question is what text comes out, and no server is needed to
// answer it. IndexKindApplyTests is where the text meets a real engine.
public sealed class IndexKindEmitTests
{
    private static IndexDefinition Index(
        string name,
        IndexKind kind,
        bool clustered = false,
        IReadOnlyList<string>? columns = null,
        Dictionary<string, string?>? extras = null) =>
        new()
        {
            Name = name,
            Kind = kind,
            IsClustered = clustered,
            Columns = (columns ?? ["Shape"]).Select(c => new IndexColumn(c, false)).ToList(),
            Extras = new ProviderExtras(extras ?? [])
        };

    private static SyncScript Script(params IndexDefinition[] indexes)
    {
        var source = new DatabaseSchema
        {
            DatabaseName = "Src",
            Tables =
            [
                new TableDefinition
                {
                    Identity = new ObjectIdentity(ObjectType.Table, "dbo", "SiteShape"),
                    Columns = [new ColumnDefinition { Name = "Shape", DataType = new DataTypeSpec("geometry"), OrdinalPosition = 1 }],
                    Indexes = indexes
                }
            ]
        };

        var target = new DatabaseSchema { DatabaseName = "Tgt" };

        return new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));
    }

    private static string IndexSql(params IndexDefinition[] indexes) =>
        Assert.Single(
            Script(indexes).Steps,
            s => s.Phase is ScriptPhase.Indexes or ScriptPhase.SecondaryIndexes).Sql;

    [Fact]
    public void A_spatial_index_carries_its_scheme_bounding_box_grids_and_cells()
    {
        var sql = IndexSql(Index("SX", IndexKind.Spatial, extras: new()
        {
            ["Spatial.Scheme"] = "GEOMETRY_GRID",
            ["Spatial.BoundingBox"] = "0, 0, 100, 100",
            ["Spatial.Grids"] = "LOW, MEDIUM, HIGH, HIGH",
            ["Spatial.CellsPerObject"] = "24"
        }));

        Assert.Equal(
            "CREATE SPATIAL INDEX [SX] ON [dbo].[SiteShape] ([Shape]) USING GEOMETRY_GRID "
            + "WITH (BOUNDING_BOX = (0, 0, 100, 100), GRIDS = (LOW, MEDIUM, HIGH, HIGH), CELLS_PER_OBJECT = 24);",
            sql);
    }

    // An AUTO_GRID scheme takes no GRIDS clause and a geography scheme no bounding box, so each option is
    // written only when the catalog had it. Defaulting one would produce DDL the server rejects.
    [Fact]
    public void An_auto_grid_spatial_index_gets_no_grids_and_no_bounding_box()
    {
        var sql = IndexSql(Index("SX", IndexKind.Spatial, extras: new()
        {
            ["Spatial.Scheme"] = "GEOGRAPHY_AUTO_GRID",
            ["Spatial.CellsPerObject"] = "12"
        }));

        Assert.Equal(
            "CREATE SPATIAL INDEX [SX] ON [dbo].[SiteShape] ([Shape]) USING GEOGRAPHY_AUTO_GRID "
            + "WITH (CELLS_PER_OBJECT = 12);",
            sql);
    }

    [Fact]
    public void A_primary_xml_index_names_its_column_only()
    {
        var sql = IndexSql(Index("PXI", IndexKind.Xml, columns: ["Body"], extras: new()
        {
            ["Xml.Kind"] = "PRIMARY_XML"
        }));

        Assert.Equal("CREATE PRIMARY XML INDEX [PXI] ON [dbo].[SiteShape] ([Body]);", sql);
    }

    [Fact]
    public void A_secondary_xml_index_names_the_primary_it_hangs_off()
    {
        var sql = IndexSql(Index("SXI", IndexKind.Xml, columns: ["Body"], extras: new()
        {
            ["Xml.Kind"] = "SECONDARY_XML",
            ["Xml.SecondaryType"] = "PATH",
            ["Xml.PrimaryIndex"] = "PXI"
        }));

        Assert.Equal(
            "CREATE XML INDEX [SXI] ON [dbo].[SiteShape] ([Body]) USING XML INDEX [PXI] FOR PATH;",
            sql);
    }

    // Within a phase the order is whatever the diff handed over, which for two indexes is alphabetical by
    // name — so a secondary XML index called A would be created before its primary called B. It gets a
    // later phase instead of relying on the names.
    [Fact]
    public void A_secondary_xml_index_is_emitted_after_the_primary_whatever_the_names_are()
    {
        var script = Script(
            Index("A_secondary", IndexKind.Xml, columns: ["Body"], extras: new()
            {
                ["Xml.Kind"] = "SECONDARY_XML",
                ["Xml.SecondaryType"] = "VALUE",
                ["Xml.PrimaryIndex"] = "Z_primary"
            }),
            Index("Z_primary", IndexKind.Xml, columns: ["Body"], extras: new()
            {
                ["Xml.Kind"] = "PRIMARY_XML"
            }));

        var indexSteps = script.Steps
            .Where(s => s.Phase is ScriptPhase.Indexes or ScriptPhase.SecondaryIndexes)
            .ToList();

        Assert.Equal(2, indexSteps.Count);
        Assert.Contains("CREATE PRIMARY XML INDEX [Z_primary]", indexSteps[0].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE XML INDEX [A_secondary]", indexSteps[1].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clustered_columnstore_names_no_columns()
    {
        var sql = IndexSql(Index("CCI", IndexKind.Columnstore, clustered: true, columns: []));

        Assert.Equal("CREATE CLUSTERED COLUMNSTORE INDEX [CCI] ON [dbo].[SiteShape];", sql);
    }

    [Fact]
    public void A_nonclustered_columnstore_names_its_columns_without_ordering_them()
    {
        var sql = IndexSql(Index("NCCI", IndexKind.Columnstore, columns: ["FactId", "Amount"]));

        Assert.Equal(
            "CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI] ON [dbo].[SiteShape] ([FactId], [Amount]);",
            sql);
    }

    // A hash index only exists inside a memory-optimized table's own CREATE TABLE, so there is no
    // statement to write. It is refused rather than skipped: the plan showed an index and the target
    // would not have one.
    [Fact]
    public void A_hash_index_is_refused_with_a_reason_and_emits_nothing()
    {
        var script = Script(Index("HX", IndexKind.Hash, columns: ["Shape"]));

        Assert.DoesNotContain(script.Steps, s => s.Phase is ScriptPhase.Indexes or ScriptPhase.SecondaryIndexes);

        var refusal = Assert.Single(script.Refusals);
        Assert.Contains("HX on dbo.SiteShape", refusal, StringComparison.Ordinal);
        Assert.Contains("memory-optimized", refusal, StringComparison.Ordinal);
        Assert.Contains("nothing is emitted", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spatial_index_with_no_scheme_is_refused_rather_than_guessed_at()
    {
        var script = Script(Index("SX", IndexKind.Spatial));

        Assert.DoesNotContain(script.Steps, s => s.Phase == ScriptPhase.Indexes);
        Assert.Contains("tessellation scheme", Assert.Single(script.Refusals), StringComparison.Ordinal);
    }

    [Fact]
    public void A_selective_xml_index_is_refused()
    {
        var script = Script(Index("SXI", IndexKind.Xml, columns: ["Body"], extras: new()
        {
            ["Xml.Kind"] = "SELECTIVE_XML"
        }));

        Assert.Contains("selective XML index", Assert.Single(script.Refusals), StringComparison.Ordinal);
    }

    // A refusal is not a step, so it cannot be counted as committed — but the script says so, since the
    // script is also read on its own.
    [Fact]
    public void A_refusal_is_stated_in_the_script_without_being_a_step()
    {
        var script = Script(Index("HX", IndexKind.Hash, columns: ["Shape"]));

        Assert.Contains("-- not emitted: HX on dbo.SiteShape", script.ToSql(), StringComparison.Ordinal);
    }

    // Msg 1934 refuses spatial and XML DDL outright when QUOTED_IDENTIFIER is off, and the script has no
    // GO in it, so the setting has to be in the same batch as the CREATE.
    [Fact]
    public void The_prologue_sets_quoted_identifier_before_the_transaction_opens()
    {
        var sql = Script(Index("SX", IndexKind.Spatial, extras: new()
        {
            ["Spatial.Scheme"] = "GEOMETRY_GRID",
            ["Spatial.BoundingBox"] = "0, 0, 1, 1"
        })).ToSql();

        Assert.Contains("SET QUOTED_IDENTIFIER ON;", sql, StringComparison.Ordinal);
        Assert.True(
            sql.IndexOf("SET QUOTED_IDENTIFIER ON;", StringComparison.Ordinal)
                < sql.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal),
            "QUOTED_IDENTIFIER must be set before the transaction opens");
    }

    [Fact]
    public void A_rowstore_index_is_emitted_exactly_as_it_always_was()
    {
        var sql = IndexSql(new IndexDefinition
        {
            Name = "IX",
            Columns = [new IndexColumn("Shape", true)],
            IncludedColumns = ["Other"],
            IsUnique = true,
            FilterExpression = "([Shape] IS NOT NULL)"
        });

        Assert.Equal(
            "CREATE UNIQUE INDEX [IX] ON [dbo].[SiteShape] ([Shape] DESC) INCLUDE ([Other]) "
            + "WHERE ([Shape] IS NOT NULL);",
            sql);
    }
}
