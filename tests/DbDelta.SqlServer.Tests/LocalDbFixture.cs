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

    // The shared pair is built once per process and torn down once, however many collections ask for it.
    //
    // xUnit gives a collection fixture one instance per collection, so splitting the classes into several
    // collections — which is what makes them run in parallel — would otherwise mean several fixtures, all
    // dropping and recreating the same two databases while tests were reading them. The alternative was
    // per-collection database names, and that is 118 call sites of `LocalDbFixture.SourceDatabase` across
    // 22 files; this is a dozen lines in one.
    //
    // Sharing them is safe because every test that touches the shared pair only reads it. Anything that
    // writes creates its own scratch database, and those names are unique across the whole suite.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<string?>? _ready;
    private static int _users;

    public async Task InitializeAsync()
    {
        await Gate.WaitAsync();

        try
        {
            _users++;
            _ready ??= BuildAsync();
        }
        finally
        {
            Gate.Release();
        }

        UnavailableReason = await _ready.ConfigureAwait(false);
        Available = UnavailableReason is null;
    }

    public async Task DisposeAsync()
    {
        await Gate.WaitAsync();

        try
        {
            // Only the last collection to finish tears the pair down. Dropping it when the first one
            // finishes would pull the databases out from under everything still running.
            if (--_users > 0 || !Available)
            {
                return;
            }

            _ready = null;
        }
        finally
        {
            Gate.Release();
        }

        await DropAsync(SourceDatabase);
        await DropAsync(TargetDatabase);
    }

    // Returns null when it worked, and the reason when LocalDB is not there — so the outcome can be awaited
    // by every collection rather than each of them probing again.
    private static async Task<string?> BuildAsync()
    {
        try
        {
            await using var connection = new SqlConnection(Master);
            await connection.OpenAsync();
        }
        catch (SqlException ex)
        {
            return ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        await DropAsync(SourceDatabase);
        await DropAsync(TargetDatabase);
        await CreateAsync(SourceDatabase, SourceScript);
        await CreateAsync(TargetDatabase, TargetScript);

        return null;
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

    // A database that exists but holds nothing, which is the provisioning case rather than a diff.
    //
    // The collation is worth being able to set: everything on one LocalDB instance shares a default, so a
    // fixture that never varies it cannot tell a script that reproduces the source's collations from one
    // that silently takes the target's. That is the whole of the replica case.
    public async Task<string> CreateEmptyTargetAsync(string suffix, string? collation = null)
    {
        var name = $"{TargetDatabase}_{suffix}";
        await DropAsync(name);

        await ExecuteOnMasterAsync(
            $"CREATE DATABASE [{name}]{(collation is null ? string.Empty : $" COLLATE {collation}")};");

        return ConnectionStringFor(name);
    }

    // The shared target has no dbo.Contact, so a child two hops from the root cannot be compared against
    // it. This one carries the table but none of its rows, which is the seeding case.
    public async Task<string> CreateChildTargetAsync(string suffix)
    {
        var connectionString = await CreateScratchTargetAsync(suffix);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            CREATE TABLE dbo.Contact (
                ContactId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Contact PRIMARY KEY,
                CompanyId   INT NOT NULL CONSTRAINT FK_Contact_Company REFERENCES dbo.Company(CompanyId),
                Email       NVARCHAR(320) NOT NULL
            );
            """,
            connection);

        await command.ExecuteNonQueryAsync();

        return connectionString;
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

    // The pool clear is not tidiness, it is the difference between 9ms and three seconds.
    //
    // ADO.NET keeps connections to a database open in its pool after the code using them is done, so
    // SET SINGLE_USER WITH ROLLBACK IMMEDIATE has to wait for them to be evicted before it can take the
    // database exclusively. Measured on this machine: 3,050ms without, 9ms with. Multiplied by the ~50
    // scratch databases this suite creates and drops, that was the majority of its whole runtime.
    //
    // ClearPool rather than ClearAllPools, which would work equally well and would reach into every other
    // pool in the process — harmless while the suite is serial, and exactly the wrong thing to leave lying
    // around for whenever it is not.
    private static async Task DropAsync(string database)
    {
        SqlConnection.ClearPool(new SqlConnection(ConnectionStringFor(database)));

        await ExecuteOnMasterAsync($"""
            IF DB_ID('{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            """);
    }

    // Enumerating every database on the instance takes server-level metadata locks, and so does creating
    // or dropping one — so the two collide, and the engine ends it either by picking a deadlock victim
    // (1205) or by killing the losing session outright. An earlier version retried the first of those and
    // was surprised by the second. Retrying is the wrong shape anyway: the collision only exists because
    // the tests do both at once, never because the product does, so the fix is to stop doing both at once.
    //
    // Creates and drops take one slot each, so they still run beside each other — that parallelism is
    // what makes the suite quick. An instance-wide read drains every slot, so it runs with the instance
    // standing still. Every create and drop in the suite goes through ExecuteOnMasterAsync, which is what
    // makes one gate enough.
    private const int ChurnSlots = 8;
    private static readonly SemaphoreSlim Churn = new(ChurnSlots, ChurnSlots);

    // Reads the whole instance with nothing being created or dropped anywhere in the suite.
    public static async Task<T> WithQuietInstanceAsync<T>(Func<Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        for (var slot = 0; slot < ChurnSlots; slot++)
        {
            await Churn.WaitAsync();
        }

        try
        {
            return await read();
        }
        finally
        {
            Churn.Release(ChurnSlots);
        }
    }

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await Churn.WaitAsync();

        try
        {
            await using var connection = new SqlConnection(Master);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            Churn.Release();
        }
    }

    private const string SourceScript = """
        CREATE SCHEMA sales;
        GO
        -- A table outside dbo, identical on both sides so it is in nobody's diff. It exists to make one
        -- thing observable: the emitter used to add every schema in the source database to the ones a plan
        -- needed, and with every table in dbo there was no way to see it — the dbo filter swallowed the
        -- evidence. A schema that holds a table is what the old code reached for.
        CREATE TABLE sales.Territory (
            TerritoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Territory PRIMARY KEY,
            Name        NVARCHAR(50) NOT NULL
        );
        GO
        CREATE TYPE dbo.PhoneNumber FROM NVARCHAR(20) NOT NULL;
        GO
        -- One sequence only on the source, and one that exists on both sides with a different increment.
        -- Without either, nothing ever exercised the sequence path and it stayed empty for months.
        CREATE SEQUENCE dbo.OrderNumber AS BIGINT START WITH 1000 INCREMENT BY 1 MINVALUE 1000 NO MAXVALUE NO CYCLE;
        GO
        CREATE SEQUENCE dbo.TicketNumber AS INT START WITH 1 INCREMENT BY 5 NO MINVALUE NO MAXVALUE CYCLE;
        GO
        -- Everything a table type can carry: no name on any constraint, because that syntax does not
        -- exist inside CREATE TYPE AS TABLE. The index is the one part that does take a name.
        CREATE TYPE dbo.IdList AS TABLE (
            Id      INT NOT NULL PRIMARY KEY,
            Ref     NVARCHAR(20) NOT NULL UNIQUE,
            Amount  DECIMAL(18,2) NOT NULL DEFAULT (0),
            Note    NVARCHAR(40) NULL,
            CHECK (Amount >= 0),
            INDEX IX_IdList_Note NONCLUSTERED (Note)
        );
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
            Phone       dbo.PhoneNumber NULL,
            CONSTRAINT CK_Contact_Email CHECK (Email LIKE '%@%')
        );
        GO
        CREATE INDEX IX_Contact_Company ON dbo.Contact (CompanyId);
        GO
        CREATE VIEW sales.vCompanySegment AS
            SELECT CompanyId, Segment FROM dbo.Company;
        GO
        SET IDENTITY_INSERT dbo.Category ON;
        INSERT INTO dbo.Category (CategoryId, Name) VALUES (1, N'Retail'), (2, N'Wholesale'), (3, N'Energy');
        SET IDENTITY_INSERT dbo.Category OFF;
        GO
        CREATE TABLE dbo.Digest (
            DigestId    INT NOT NULL CONSTRAINT PK_Digest PRIMARY KEY,
            A           NVARCHAR(10) NULL,
            B           NVARCHAR(10) NULL,
            Amount      FLOAT NULL
        );
        GO
        INSERT INTO dbo.Digest (DigestId, A, B, Amount) VALUES (1, NULL, N'x', -25), (2, N'x', NULL, 25), (3, N'', N'x', NULL);
        GO
        -- A leading comment on purpose. Every definition written by hand for a test used to start with
        -- the word CREATE, which is why CreateOrAlter's failure to look past a comment went unnoticed: the
        -- object emitted as a plain CREATE and failed on the second apply.
        CREATE VIEW sales.vActiveSegments AS
            SELECT CompanyId, Segment FROM sales.vCompanySegment WHERE Segment IS NOT NULL;
        GO
        CREATE PROCEDURE dbo.usp_GetCompany @Id INT AS
            SELECT * FROM dbo.Company WHERE CompanyId = @Id;
        GO
        -- A procedure whose parameter is a table type, which is the case that made routine dependencies
        -- worth looking at. sys.sql_expression_dependencies does report the type — but in a different id
        -- space, class 6, keyed by user_type_id — so the reader's join to sys.objects dropped it and the
        -- closure never knew this procedure needed dbo.IdList to exist first.
        CREATE PROCEDURE dbo.usp_TagCompanies @Ids dbo.IdList READONLY, @Phone dbo.PhoneNumber AS
            SELECT c.CompanyId, @Phone AS Phone
            FROM dbo.Company c JOIN @Ids i ON i.Id = c.CompanyId;
        GO
        -- Rows for parent closure to walk. Contoso sits in category 3, which the target does not have,
        -- and the contact sits on Contoso: seeding the contact needs both, two hops up.
        SET IDENTITY_INSERT dbo.Company ON;
        INSERT INTO dbo.Company (CompanyId, CompanyName, Segment, CategoryId, RatingBand, Total)
        VALUES (1, N'Northwind', N'Retail', 1, 3, 100.00), (2, N'Contoso', N'Energy', 3, 4, 250.00);
        SET IDENTITY_INSERT dbo.Company OFF;
        GO
        SET IDENTITY_INSERT dbo.Contact ON;
        INSERT INTO dbo.Contact (ContactId, CompanyId, Email) VALUES (1, 2, N'ops@contoso.example');
        SET IDENTITY_INSERT dbo.Contact OFF;
        GO
        -- More rows than the detail reader fetches in one go, and more than fit in one INSERT batch, so
        -- both caps are exercised by something rather than assumed to be fine.
        CREATE TABLE dbo.Metric (
            MetricId  INT NOT NULL CONSTRAINT PK_Metric PRIMARY KEY,
            Label   NVARCHAR(60) NOT NULL,
            Amount  DECIMAL(18,2) NOT NULL,
            At      DATETIME2 NULL
        );
        GO
        INSERT INTO dbo.Metric (MetricId, Label, Amount, At)
        SELECT TOP (1200)
            ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
            N'row ' + CONVERT(nvarchar(10), ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
            ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.5,
            DATEADD(minute, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-01-01T00:00:00')
        FROM sys.all_objects a CROSS JOIN sys.all_objects b;
        GO
        """;

    // Deliberately behind the source in four ways: a narrower Segment, no RatingBand, no rating
    // index, and a different view body.
    private const string TargetScript = """
        CREATE SCHEMA sales;
        GO
        -- A table outside dbo, identical on both sides so it is in nobody's diff. It exists to make one
        -- thing observable: the emitter used to add every schema in the source database to the ones a plan
        -- needed, and with every table in dbo there was no way to see it — the dbo filter swallowed the
        -- evidence. A schema that holds a table is what the old code reached for.
        CREATE TABLE sales.Territory (
            TerritoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Territory PRIMARY KEY,
            Name        NVARCHAR(50) NOT NULL
        );
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
        SET IDENTITY_INSERT dbo.Category ON;
        INSERT INTO dbo.Category (CategoryId, Name) VALUES (1, N'Retail'), (2, N'Wholesale Ltd'), (4, N'Obsolete');
        SET IDENTITY_INSERT dbo.Category OFF;
        GO
        CREATE TABLE dbo.Digest (
            DigestId    INT NOT NULL CONSTRAINT PK_Digest PRIMARY KEY,
            A           NVARCHAR(10) NULL,
            B           NVARCHAR(10) NULL,
            Amount      FLOAT NULL
        );
        GO
        INSERT INTO dbo.Digest (DigestId, A, B, Amount) VALUES (1, NULL, N'x', -25), (2, N'x', NULL, 25), (3, N'', N'x', NULL);
        GO
        CREATE TABLE dbo.SegmentLegacy (
            Id INT NOT NULL CONSTRAINT PK_SegmentLegacy PRIMARY KEY
        );
        GO
        -- Same name, different increment and cycling: the ALTER path rather than the CREATE path.
        CREATE SEQUENCE dbo.TicketNumber AS INT START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE NO CYCLE;
        GO
        -- Only on the target, so it is a drop.
        CREATE SEQUENCE dbo.LegacyCounter AS INT START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE NO CYCLE;
        GO
        -- Present but empty, so every one of the source's 1200 rows is an insert.
        CREATE TABLE dbo.Metric (
            MetricId  INT NOT NULL CONSTRAINT PK_Metric PRIMARY KEY,
            Label   NVARCHAR(60) NOT NULL,
            Amount  DECIMAL(18,2) NOT NULL,
            At      DATETIME2 NULL
        );
        GO
        """;
}

// Four collections rather than one, because xUnit runs collections in parallel and everything in a single
// collection in sequence. They all share one process-wide pair of databases, so the split costs nothing in
// setup — it exists only to let four classes be in flight at once.
//
// Membership is balanced by measured cost rather than by subject, so the slowest classes do not queue behind
// one another. That means a class's collection says nothing about what it tests, which is worth knowing
// before looking for a meaning that is not there.
[CollectionDefinition(nameof(LocalDbCollection))]
public sealed class LocalDbCollection : ICollectionFixture<LocalDbFixture>;

[CollectionDefinition(nameof(LocalDbCollectionB))]
public sealed class LocalDbCollectionB : ICollectionFixture<LocalDbFixture>;

[CollectionDefinition(nameof(LocalDbCollectionC))]
public sealed class LocalDbCollectionC : ICollectionFixture<LocalDbFixture>;

[CollectionDefinition(nameof(LocalDbCollectionD))]
public sealed class LocalDbCollectionD : ICollectionFixture<LocalDbFixture>;
