using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// Column changes that ALTER COLUMN cannot make on its own, applied to a real server. The first test is the
// reported shape: a table copied through a linked server on the legacy ODBC driver, which loses identity,
// widens datetime to datetime2 and turns nvarchar(max) into ntext. The script DbDelta emitted for it
// failed to compile (IDENTITY inside ALTER COLUMN, error 156), and with that removed failed again on the
// default holding LastUpdated (error 4922). A snapshot of the script caught neither; only running it does.
//
// Every pair here is its own, because each test writes to its target.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollection))]
public sealed class ColumnDependencyApplyTests
{
    private const string Collation = "Latin1_General_CI_AS";

    private readonly LocalDbFixture _fixture;

    public ColumnDependencyApplyTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_table_that_lost_its_identity_is_rebuilt_with_its_rows_keys_grants_and_triggers()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string shared = """
            CREATE TABLE dbo.Item (
                ItemId     INT NOT NULL CONSTRAINT PK_Item PRIMARY KEY,
                CategoryId INT NOT NULL CONSTRAINT FK_Item_Category REFERENCES dbo.Category (CategoryId)
            );
            """;

        const string view = "CREATE VIEW dbo.vCategoryAll AS SELECT * FROM dbo.Category;";
        const string trigger = "CREATE TRIGGER dbo.TR_Category_Touch ON dbo.Category AFTER UPDATE AS SET NOCOUNT ON;";
        const string disable = "DISABLE TRIGGER dbo.TR_Category_Touch ON dbo.Category;";

        var (_, after) = await ApplyAsync(
            "RebuildSrc",
            "RebuildTgt",
            [
                """
                CREATE TABLE dbo.Category (
                    CategoryId  INT IDENTITY(1,2) NOT NULL,
                    Name        VARCHAR(50) COLLATE Latin1_General_CI_AS NULL,
                    Notes       NVARCHAR(MAX) COLLATE Latin1_General_CI_AS NULL,
                    LastUpdated DATETIME NULL CONSTRAINT DF_Category_LastUpdated DEFAULT (getdate()),
                    ReviewedOn  DATETIME NULL,
                    CONSTRAINT PK_Category PRIMARY KEY CLUSTERED (CategoryId)
                );
                """,
                shared,
                "CREATE TABLE dbo.Tag (TagId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tag PRIMARY KEY, Label NVARCHAR(20) NULL);",
                view,
                trigger,
                disable
            ],
            [
                """
                CREATE TABLE dbo.Category (
                    CategoryId  INT NOT NULL,
                    Name        VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
                    Notes       NTEXT COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
                    LastUpdated DATETIME2 NULL CONSTRAINT DF_Category_LastUpdated DEFAULT (getdate()),
                    ReviewedOn  DATETIME2 NULL,
                    CONSTRAINT PK_Category PRIMARY KEY CLUSTERED (CategoryId)
                );
                """,
                """
                INSERT dbo.Category (CategoryId, Name, Notes, LastUpdated, ReviewedOn)
                VALUES (1, 'a', N'one', '2026-01-01 10:00:00.123', NULL),
                       (3, 'b', N'two', '2026-01-02 11:00:00.457', '2026-01-03');
                """,
                shared,
                "INSERT dbo.Item VALUES (10, 1), (11, 3);",
                // A different seed is a different identity, and the counter must survive the copy: the top
                // row was deleted, so the next value is 103, not the 102 the remaining rows would suggest.
                "CREATE TABLE dbo.Tag (TagId INT IDENTITY(100,1) NOT NULL CONSTRAINT PK_Tag PRIMARY KEY, Label NVARCHAR(20) NULL);",
                "INSERT dbo.Tag (Label) VALUES (N'x'), (N'y'), (N'z'); DELETE dbo.Tag WHERE TagId = 102;",
                view,
                trigger,
                disable,
                "CREATE STATISTICS ST_Category_Name ON dbo.Category (Name);",
                "CREATE ROLE CategoryReader; GRANT SELECT ON dbo.Category TO CategoryReader;"
            ],
            assertOnTarget: async (connectionString, script) =>
            {
                Assert.Empty(script.Refusals);
                Assert.DoesNotContain("ALTER COLUMN", script.ToSql(), StringComparison.Ordinal);

                Assert.Equal(
                    "1|a|one|2026-01-01 10:00:00.123;3|b|two|2026-01-02 11:00:00.457",
                    await ScalarAsync(connectionString, """
                        SELECT STRING_AGG(CONCAT(CategoryId, '|', Name, '|', Notes, '|',
                            CONVERT(varchar(23), LastUpdated, 121)), ';') WITHIN GROUP (ORDER BY CategoryId)
                        FROM dbo.Category;
                        """));

                // Increment 2 from the highest copied value, and the default back in place.
                Assert.Equal("5|1", await ScalarAsync(connectionString, """
                    INSERT dbo.Category (Name) VALUES ('c');
                    SELECT CONCAT(CategoryId, '|', IIF(LastUpdated IS NULL, 0, 1)) FROM dbo.Category WHERE Name = 'c';
                    """));

                Assert.Equal("103", await ScalarAsync(connectionString, """
                    INSERT dbo.Tag (Label) VALUES (N'w');
                    SELECT CAST(MAX(TagId) AS varchar(10)) FROM dbo.Tag;
                    """));

                Assert.Equal("SELECT", await ScalarAsync(connectionString, """
                    SELECT permission_name FROM sys.database_permissions
                    WHERE major_id = OBJECT_ID('dbo.Category') AND grantee_principal_id = DATABASE_PRINCIPAL_ID('CategoryReader');
                    """));

                // The view was created against the old columns, and SELECT * binds them at creation.
                Assert.Equal("datetime", await ScalarAsync(connectionString, """
                    SELECT TYPE_NAME(system_type_id) FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.vCategoryAll') AND name = 'LastUpdated';
                    """));
            });

        var category = after.Tables.Single(t => t.Identity.Name == "Category");
        Assert.Contains(category.Statistics, s => s.Name == "ST_Category_Name");
        Assert.True(after.Triggers.Single(t => t.Identity.Name == "TR_Category_Touch").IsDisabled);
        Assert.Contains(after.Tables.Single(t => t.Identity.Name == "Item").ForeignKeys, f => f.Name == "FK_Item_Category");
    }

    [SkippableFact]
    public async Task Everything_holding_a_column_comes_down_and_goes_back_up_around_its_alter()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        await ApplyAsync(
            "InPlaceSrc",
            "InPlaceTgt",
            [
                """
                CREATE TABLE dbo.Parent (
                    ParentId BIGINT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY,
                    Code     VARCHAR(20) NOT NULL CONSTRAINT UQ_Parent_Code UNIQUE,
                    Score    BIGINT NULL CONSTRAINT DF_Parent_Score DEFAULT (0) CONSTRAINT CK_Parent_Score CHECK (Score >= 0),
                    Band     AS (Score / 10),
                    Status   INT NULL CONSTRAINT DF_Parent_Status DEFAULT (1),
                    Doubled  AS (Score * 2)
                );
                """,
                "CREATE INDEX IX_Parent_Positive ON dbo.Parent (ParentId) WHERE Score > 0;",
                "CREATE INDEX IX_Parent_Band ON dbo.Parent (Band);",
                """
                CREATE TABLE dbo.Child (
                    ChildId  INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY,
                    ParentId BIGINT NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (ParentId)
                );
                """
            ],
            [
                """
                CREATE TABLE dbo.Parent (
                    ParentId INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY,
                    Code     VARCHAR(10) NOT NULL CONSTRAINT UQ_Parent_Code UNIQUE,
                    Score    INT NULL CONSTRAINT DF_Parent_Score DEFAULT (0) CONSTRAINT CK_Parent_Score CHECK (Score >= 0),
                    Band     AS (Score / 10),
                    Status   INT NULL CONSTRAINT DF_Parent_Status DEFAULT (0),
                    Doubled  AS (Score * 3),
                    Legacy   INT NULL CONSTRAINT DF_Parent_Legacy DEFAULT (0)
                );
                """,
                "CREATE INDEX IX_Parent_Positive ON dbo.Parent (ParentId) WHERE Score > 0;",
                "CREATE INDEX IX_Parent_Band ON dbo.Parent (Band);",
                "CREATE STATISTICS ST_Parent_Score ON dbo.Parent (Score);",
                """
                CREATE TABLE dbo.Child (
                    ChildId  INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY,
                    ParentId INT NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (ParentId)
                );
                """,
                "INSERT dbo.Parent (ParentId, Code, Score) VALUES (1, 'a', 5), (2, 'b', 15);",
                "INSERT dbo.Child VALUES (1, 1), (2, 2);"
            ],
            assertOnTarget: async (connectionString, script) =>
            {
                Assert.Empty(script.Refusals);

                // One key between two changing tables, dropped once and put back once.
                Assert.Single(script.Steps, s => s.Sql.Contains("DROP CONSTRAINT [FK_Child_Parent]", StringComparison.Ordinal));
                Assert.Single(script.Steps, s => s.Sql.Contains("ADD CONSTRAINT [FK_Child_Parent]", StringComparison.Ordinal));

                Assert.Equal("1:a:5:0:10;2:b:15:1:30", await ScalarAsync(connectionString, """
                    SELECT STRING_AGG(CONCAT(ParentId, ':', Code, ':', Score, ':', Band, ':', Doubled), ';')
                        WITHIN GROUP (ORDER BY ParentId)
                    FROM dbo.Parent;
                    """));

                Assert.Equal("1", await ScalarAsync(connectionString, """
                    INSERT dbo.Parent (ParentId, Code) VALUES (3, 'c');
                    SELECT CAST(Status AS varchar(10)) FROM dbo.Parent WHERE ParentId = 3;
                    """));

                Assert.Equal("ST_Parent_Score", await ScalarAsync(connectionString,
                    "SELECT name FROM sys.stats WHERE object_id = OBJECT_ID('dbo.Parent') AND user_created = 1;"));
            });
    }

    [SkippableFact]
    public async Task A_rebuild_a_schema_bound_view_prevents_is_refused_and_the_rest_still_applies()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string view = """
            CREATE VIEW dbo.vCategoryCount WITH SCHEMABINDING AS SELECT COUNT_BIG(*) AS Categories FROM dbo.Category;
            """;

        var (source, after) = await ApplyAsync(
            "BoundSrc",
            "BoundTgt",
            [
                """
                CREATE TABLE dbo.Category (
                    CategoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
                    ReviewedOn DATETIME NULL
                );
                """,
                view
            ],
            [
                """
                CREATE TABLE dbo.Category (
                    CategoryId INT NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
                    ReviewedOn DATETIME2 NULL
                );
                """,
                view,
                "INSERT dbo.Category VALUES (1, '2026-01-03');"
            ],
            assertOnTarget: (_, script) =>
            {
                var refusal = Assert.Single(script.Refusals);
                Assert.Contains("dbo.vCategoryCount is schema-bound to it", refusal, StringComparison.Ordinal);
                return Task.CompletedTask;
            },
            expectConverged: false);

        var remaining = Assert.Single(new SchemaComparer().Compare(source, after).Differing);
        var column = Assert.Single(remaining.DifferingChildren);
        Assert.Equal("CategoryId", column.Identity.Name);
        Assert.Equal("Identity", Assert.Single(column.Properties).Property);
    }

    // The emitter falls back to searching stored expressions when the catalog's column list is empty, so the
    // apply tests above would pass with the reader broken. This asserts the catalog is what answered.
    [SkippableFact]
    public async Task The_reader_records_what_holds_each_column_and_what_stops_a_rebuild()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string name = "HoldersRead";
        var connectionString = await _fixture.CreateEmptyTargetAsync(name, Collation);

        try
        {
            foreach (var batch in new[]
            {
                """
                CREATE TABLE dbo.Parent (
                    ParentId INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY,
                    Low      INT NULL,
                    High     INT NULL,
                    Spread   AS (High - Low),
                    CONSTRAINT CK_Parent_Range CHECK (Low <= High)
                );
                """,
                "CREATE STATISTICS ST_Parent_Low ON dbo.Parent (Low, High) WITH NORECOMPUTE;",
                "CREATE VIEW dbo.vParentLow WITH SCHEMABINDING AS SELECT Low FROM dbo.Parent;",
                """
                CREATE TABLE dbo.Audit (
                    AuditId   INT NOT NULL CONSTRAINT PK_Audit PRIMARY KEY,
                    ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                    ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
                ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.AuditHistory));
                """
            })
            {
                await ExecuteAsync(connectionString, batch);
            }

            var schema = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var parent = schema.Tables.Single(t => t.Identity.Name == "Parent");

            Assert.Empty(schema.ReadWarnings);
            Assert.Equal(["High", "Low"], parent.Columns.Single(c => c.Name == "Spread").ComputedFrom.Order());
            Assert.Equal(["High", "Low"], parent.CheckConstraints.Single().Columns.Order());

            var statistics = Assert.Single(parent.Statistics);
            Assert.Equal(["Low", "High"], statistics.Columns);
            Assert.True(statistics.NoRecompute);

            var bound = Assert.Single(parent.SchemaBoundReferences);
            Assert.Equal(new ObjectIdentity(ObjectType.View, "dbo", "vParentLow"), bound.Module);
            Assert.Equal(["Low"], bound.Columns);

            Assert.Null(parent.Extras[TableExtras.RebuildBlockers]);
            Assert.Contains("system-versioned", schema.Tables.Single(t => t.Identity.Name == "Audit").Extras[TableExtras.RebuildBlockers]);
            Assert.Contains("system-versioned", schema.Tables.Single(t => t.Identity.Name == "AuditHistory").Extras[TableExtras.RebuildBlockers]);
        }
        finally
        {
            await _fixture.DropScratchAsync(name);
        }
    }

    private async Task<(DatabaseSchema Source, DatabaseSchema After)> ApplyAsync(
        string sourceName,
        string targetName,
        string[] sourceDdl,
        string[] targetDdl,
        Func<string, Core.Scripting.SyncScript, Task> assertOnTarget,
        bool expectConverged = true)
    {
        var sourceConnection = await _fixture.CreateEmptyTargetAsync(sourceName, Collation);
        var targetConnection = await _fixture.CreateEmptyTargetAsync(targetName, Collation);

        try
        {
            foreach (var batch in sourceDdl)
            {
                await ExecuteAsync(sourceConnection, batch);
            }

            foreach (var batch in targetDdl)
            {
                await ExecuteAsync(targetConnection, batch);
            }

            var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
            var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();
            var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

            var result = await new SqlServerScriptExecutor().ExecuteAsync(targetConnection, script);
            Assert.True(result.Outcome == ApplyOutcome.Committed, $"{result.ServerMessage}\n\n{script.ToSql()}");

            await assertOnTarget(targetConnection, script);

            var after = await new SqlServerSchemaReader(targetConnection).ReadAsync();

            if (expectConverged)
            {
                Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
            }

            return (source, after);
        }
        finally
        {
            await _fixture.DropScratchAsync(sourceName);
            await _fixture.DropScratchAsync(targetName);
        }
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
