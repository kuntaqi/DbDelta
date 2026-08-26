using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// LocalDB is present on the machine this was written on but is not guaranteed elsewhere, so the
// fixture probes rather than assumes. Tests skip when it is missing instead of failing, which keeps
// a clone on a machine without SQL Server green.
public sealed class LocalDbFixture : IAsyncLifetime
{
    private const string Master =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    public const string SourceDatabase = "DbDelta_IntegrationTest_Src";
    public const string TargetDatabase = "DbDelta_IntegrationTest_Tgt";

    public bool Available { get; private set; }

    public string? UnavailableReason { get; private set; }

    public static string ConnectionStringFor(string database) =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={database};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    public async Task InitializeAsync()
    {
        try
        {
            await using var connection = new SqlConnection(Master);
            await connection.OpenAsync();
            Available = true;
        }
        catch (SqlException ex)
        {
            UnavailableReason = ex.Message;
            return;
        }
        catch (InvalidOperationException ex)
        {
            UnavailableReason = ex.Message;
            return;
        }

        await DropAsync(SourceDatabase);
        await DropAsync(TargetDatabase);
        await CreateAsync(SourceDatabase, SourceScript);
        await CreateAsync(TargetDatabase, TargetScript);
    }

    public async Task DisposeAsync()
    {
        if (!Available)
        {
            return;
        }

        await DropAsync(SourceDatabase);
        await DropAsync(TargetDatabase);
    }

    // The emitter test mutates its target, so it gets a disposable copy rather than the shared one
    // the read-only tests assert against.
    public async Task<string> CreateScratchTargetAsync(string suffix)
    {
        var name = $"{TargetDatabase}_{suffix}";
        await DropAsync(name);
        await CreateAsync(name, TargetScript);
        return ConnectionStringFor(name);
    }

    public async Task DropScratchAsync(string suffix) => await DropAsync($"{TargetDatabase}_{suffix}");

    private static async Task CreateAsync(string database, string script)
    {
        await ExecuteOnMasterAsync($"CREATE DATABASE [{database}];");

        await using var connection = new SqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync();

        foreach (var batch in SplitBatches(script))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    // CREATE SCHEMA, VIEW and PROCEDURE each have to start their own batch, which is exactly the
    // T-SQL constraint the emitter has to work around later. Line-based so CRLF checkouts still split.
    private static IEnumerable<string> SplitBatches(string script)
    {
        var batch = new List<string>();

        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var text = string.Join('\n', batch);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }

                batch.Clear();
                continue;
            }

            batch.Add(line);
        }

        var tail = string.Join('\n', batch);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            yield return tail;
        }
    }

    private static async Task DropAsync(string database) =>
        await ExecuteOnMasterAsync($"""
            IF DB_ID('{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            """);

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(Master);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private const string SourceScript = """
        CREATE SCHEMA sales;
        GO
        CREATE TABLE dbo.Category (
            CategoryId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
            Name        NVARCHAR(50) NOT NULL CONSTRAINT UQ_Category_Name UNIQUE
        );
        GO
        CREATE TABLE dbo.Company (
            CompanyId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Company PRIMARY KEY,
            CompanyName NVARCHAR(200) NOT NULL,
            Segment     NVARCHAR(40) NULL,
            CategoryId  INT NULL CONSTRAINT FK_Company_Category REFERENCES dbo.Category(CategoryId),
            RatingBand  TINYINT NULL,
            Total       DECIMAL(18,2) NOT NULL CONSTRAINT DF_Company_Total DEFAULT (0),
            CONSTRAINT CK_Company_Rating CHECK (RatingBand IS NULL OR RatingBand BETWEEN 1 AND 5)
        );
        GO
        CREATE INDEX IX_Company_Rating ON dbo.Company (RatingBand) INCLUDE (CompanyName);
        GO
        CREATE UNIQUE INDEX UX_Company_Segment ON dbo.Company (Segment) WHERE Segment IS NOT NULL;
        GO
        CREATE TABLE dbo.Contact (
            ContactId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Contact PRIMARY KEY,
            CompanyId   INT NOT NULL CONSTRAINT FK_Contact_Company REFERENCES dbo.Company(CompanyId),
            Email       NVARCHAR(320) NOT NULL,
            CONSTRAINT CK_Contact_Email CHECK (Email LIKE '%@%')
        );
        GO
        CREATE INDEX IX_Contact_Company ON dbo.Contact (CompanyId);
        GO
        CREATE VIEW sales.vCompanySegment AS
            SELECT CompanyId, Segment FROM dbo.Company;
        GO
        CREATE PROCEDURE dbo.usp_GetCompany @Id INT AS
            SELECT * FROM dbo.Company WHERE CompanyId = @Id;
        GO
        """;

    // Deliberately behind the source in four ways: a narrower Segment, no RatingBand, no rating
    // index, and a different view body.
    private const string TargetScript = """
        CREATE SCHEMA sales;
        GO
        CREATE TABLE dbo.Category (
            CategoryId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
            Name        NVARCHAR(50) NOT NULL CONSTRAINT UQ_Category_Name UNIQUE
        );
        GO
        CREATE TABLE dbo.Company (
            CompanyId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Company PRIMARY KEY,
            CompanyName NVARCHAR(200) NOT NULL,
            Segment     NVARCHAR(20) NULL,
            CategoryId  INT NULL CONSTRAINT FK_Company_Category REFERENCES dbo.Category(CategoryId),
            Total       DECIMAL(18,2) NOT NULL CONSTRAINT DF_Company_Total DEFAULT (0)
        );
        GO
        CREATE UNIQUE INDEX UX_Company_Segment ON dbo.Company (Segment) WHERE Segment IS NOT NULL;
        GO
        CREATE VIEW sales.vCompanySegment AS
            SELECT CompanyId FROM dbo.Company;
        GO
        CREATE PROCEDURE dbo.usp_GetCompany @Id INT AS
            SELECT * FROM dbo.Company WHERE CompanyId = @Id;
        GO
        CREATE TABLE dbo.SegmentLegacy (
            Id INT NOT NULL CONSTRAINT PK_SegmentLegacy PRIMARY KEY
        );
        GO
        """;
}

[CollectionDefinition(nameof(LocalDbCollection))]
public sealed class LocalDbCollection : ICollectionFixture<LocalDbFixture>;
