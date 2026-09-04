using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// The reported repro, plus every other kind sys.indexes can report, provisioned into an empty target for
// real. Before this, type_desc was collapsed to the single boolean IsClustered, so a spatial index was
// emitted as CREATE INDEX over a geometry column: error 1978, and because the script is one transaction,
// the whole apply rolled back. One index nobody was thinking about made a database impossible to provision.
//
// Its own source and target, since the source has to hold the indexes and the shared pair is read-only by
// contract.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionC))]
public sealed class IndexKindApplyTests
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE dbo.SiteShape (
            SiteId INT NOT NULL CONSTRAINT PK_SiteShape PRIMARY KEY,
            Name   NVARCHAR(80) NOT NULL,
            Shape  GEOMETRY NULL,
            Region GEOGRAPHY NULL
        );
        """,
        """
        CREATE TABLE dbo.Doc (
            DocId INT NOT NULL CONSTRAINT PK_Doc PRIMARY KEY,
            Body  XML NULL
        );
        """,
        "CREATE TABLE dbo.Fact (FactId INT NOT NULL, Amount DECIMAL(18,2) NOT NULL, Label NVARCHAR(40) NULL);",
        "CREATE TABLE dbo.FactArchive (FactId INT NOT NULL, Amount DECIMAL(18,2) NOT NULL);",
        "CREATE TABLE dbo.FactOrdered (FactId INT NOT NULL, Amount DECIMAL(18,2) NOT NULL, Label NVARCHAR(20) NULL);",
        """
        CREATE SPATIAL INDEX SX_SiteShape_Shape ON dbo.SiteShape (Shape)
            USING GEOMETRY_GRID
            WITH (BOUNDING_BOX = (0, 0, 100, 100), GRIDS = (LOW, MEDIUM, HIGH, HIGH), CELLS_PER_OBJECT = 24);
        """,
        """
        CREATE SPATIAL INDEX SX_SiteShape_Region ON dbo.SiteShape (Region)
            USING GEOGRAPHY_AUTO_GRID WITH (CELLS_PER_OBJECT = 12);
        """,
        "CREATE INDEX IX_SiteShape_Name ON dbo.SiteShape (Name);",
        "CREATE PRIMARY XML INDEX PXI_Doc_Body ON dbo.Doc (Body);",
        "CREATE XML INDEX SXI_Doc_Body_Path ON dbo.Doc (Body) USING XML INDEX PXI_Doc_Body FOR PATH;",
        "CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Fact ON dbo.Fact (FactId, Amount);",
        "CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactArchive ON dbo.FactArchive;",
        "CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactOrdered ON dbo.FactOrdered ORDER (FactId, Amount);"
    ];

    private readonly LocalDbFixture _fixture;

    public IndexKindApplyTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Every_index_kind_is_read_as_itself_and_applied_as_itself()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string src = "IdxKindSrc";
        const string tgt = "IdxKindTgt";

        var sourceConnection = await _fixture.CreateEmptyTargetAsync(src);
        var targetConnection = await _fixture.CreateEmptyTargetAsync(tgt);

        try
        {
            foreach (var batch in Ddl)
            {
                await ExecuteAsync(sourceConnection, batch);
            }

            var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();

            // Read as itself, which is the half of the defect that happens before any SQL is written.
            Assert.Equal(IndexKind.Spatial, Index(source, "SiteShape", "SX_SiteShape_Shape").Kind);
            Assert.Equal(IndexKind.Spatial, Index(source, "SiteShape", "SX_SiteShape_Region").Kind);
            Assert.Equal(IndexKind.Rowstore, Index(source, "SiteShape", "IX_SiteShape_Name").Kind);
            Assert.Equal(IndexKind.Xml, Index(source, "Doc", "PXI_Doc_Body").Kind);
            Assert.Equal(IndexKind.Xml, Index(source, "Doc", "SXI_Doc_Body_Path").Kind);
            Assert.Equal(IndexKind.Columnstore, Index(source, "Fact", "NCCI_Fact").Kind);
            Assert.Equal(IndexKind.Columnstore, Index(source, "FactArchive", "CCI_FactArchive").Kind);

            // A columnstore reports its columns as included columns with key ordinal zero, so the old
            // reader gave a nonclustered one no key columns at all and the emitter wrote an empty column
            // list. The set is asserted rather than the sequence on purpose: a nonclustered columnstore has
            // no column order to preserve, and the catalog hands them back alphabetically whatever the DDL
            // said, so both sides read back the same way and the compare still converges.
            Assert.Equal(
                ["Amount", "FactId"],
                Index(source, "Fact", "NCCI_Fact").Columns.Select(c => c.Name).Order());

            // A clustered columnstore covers the whole table and states no columns...
            Assert.Empty(Index(source, "FactArchive", "CCI_FactArchive").Columns);
            Assert.Empty(Index(source, "FactArchive", "CCI_FactArchive").IncludedColumns);

            // ...unless it is ordered, where the ORDER list is the one thing it does state, in its own
            // order. Nothing in sys.indexes separates the two, so an unordered index would otherwise be
            // emitted for an ordered one and compare identical to it afterwards.
            Assert.Equal(
                ["FactId", "Amount"],
                Index(source, "FactOrdered", "CCI_FactOrdered").Columns.Select(c => c.Name));

            var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));
            var sql = script.ToSql();

            Assert.Empty(script.Refusals);
            Assert.Contains("SET QUOTED_IDENTIFIER ON;", sql, StringComparison.Ordinal);
            Assert.Contains("USING GEOMETRY_GRID WITH (BOUNDING_BOX = (0, 0, 100, 100)", sql, StringComparison.Ordinal);
            Assert.Contains("GRIDS = (LOW, MEDIUM, HIGH, HIGH)", sql, StringComparison.Ordinal);
            Assert.Contains("CELLS_PER_OBJECT = 24", sql, StringComparison.Ordinal);
            Assert.Contains("USING GEOGRAPHY_AUTO_GRID WITH (CELLS_PER_OBJECT = 12)", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE PRIMARY XML INDEX [PXI_Doc_Body]", sql, StringComparison.Ordinal);
            Assert.Contains("USING XML INDEX [PXI_Doc_Body] FOR PATH", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [CCI_FactArchive] ON [dbo].[FactArchive];", sql, StringComparison.Ordinal);
            Assert.Contains(
                "CREATE CLUSTERED COLUMNSTORE INDEX [CCI_FactOrdered] ON [dbo].[FactOrdered] ORDER ([FactId], [Amount]);",
                sql,
                StringComparison.Ordinal);
            Assert.Contains("CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_Fact]", sql, StringComparison.Ordinal);

            // An AUTO_GRID scheme rejects a GRIDS clause, so the one that has no grid densities must not
            // be given any.
            Assert.DoesNotContain("GEOGRAPHY_AUTO_GRID WITH (GRIDS", sql, StringComparison.Ordinal);

            var result = await new SqlServerScriptExecutor().ExecuteAsync(targetConnection, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(targetConnection).ReadAsync();

            Assert.Equal(IndexKind.Spatial, Index(after, "SiteShape", "SX_SiteShape_Shape").Kind);
            Assert.Equal(IndexKind.Xml, Index(after, "Doc", "SXI_Doc_Body_Path").Kind);
            Assert.Equal(IndexKind.Columnstore, Index(after, "FactArchive", "CCI_FactArchive").Kind);

            // The tessellation has to survive the round trip, not just the kind: a spatial index recreated
            // with a different bounding box is a different index that happens to compare equal on name.
            Assert.Equal(
                "0, 0, 100, 100",
                Index(after, "SiteShape", "SX_SiteShape_Shape").Extras["Spatial.BoundingBox"]);

            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(src);
            await _fixture.DropScratchAsync(tgt);
        }
    }

    private static IndexDefinition Index(DatabaseSchema schema, string table, string name) =>
        schema.Tables.Single(t => t.Identity.Name == table)
            .Indexes.Single(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
