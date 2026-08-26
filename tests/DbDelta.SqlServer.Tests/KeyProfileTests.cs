using DbDelta.Core.Data;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// A table with no primary key is not automatically a table with no key. This measures every candidate
// in one pass so the picker can offer what actually works instead of asking the user to guess a column
// and wait for a rejection each time.
[Collection(nameof(LocalDbCollection))]
public sealed class KeyProfileTests
{
    private readonly LocalDbFixture _fixture;

    public KeyProfileTests(LocalDbFixture fixture) => _fixture = fixture;

    private const string TableName = "dbo.KeylessProbe";

    private const string CreateScript = """
        CREATE TABLE dbo.KeylessProbe (
            Slip        INT NOT NULL,
            Area        NVARCHAR(20) NOT NULL,
            Operator    NVARCHAR(20) NULL,
            Payload     NVARCHAR(50) NOT NULL,
            Blob        NTEXT NULL
        );
        INSERT INTO dbo.KeylessProbe (Slip, Area, Operator, Payload) VALUES
            (1, N'in',  N'ann', N'a'),
            (2, N'in',  NULL,   N'b'),
            (3, N'out', N'bob', N'c'),
            (4, N'out', N'ann', N'd');
        """;

    [SkippableFact]
    public async Task Every_candidate_column_is_measured_in_one_pass()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var connectionString = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        await ExecuteAsync(connectionString, CreateScript);

        try
        {
            var table = (await new SqlServerSchemaReader(connectionString).ReadAsync())
                .Tables.Single(t => t.Identity.QualifiedName == TableName);

            Assert.Null(table.PrimaryKey);

            var profile = await new SqlServerKeyUniquenessChecker(connectionString)
                .ProfileAsync(table, table.Columns.Select(c => c.Name).ToList());

            Assert.True(profile.WasProbed);
            Assert.Null(profile.Problem);
            Assert.Equal(4, profile.RowCount);

            // Slip and Payload are distinct per row. Area repeats, Operator has a NULL, and Blob is NTEXT
            // which COUNT(DISTINCT ...) refuses outright — so it is left out of the scan rather than
            // being allowed to fail the whole query.
            Assert.Equal(["Slip", "Payload"], profile.UniqueColumns);
            Assert.DoesNotContain("Blob", profile.Columns.Select(c => c.Column));

            var operatorColumn = profile.Columns.Single(c => c.Column == "Operator");
            Assert.Equal(1, operatorColumn.NullRows);
            Assert.False(operatorColumn.CouldBeKey(profile.RowCount));

            Assert.Equal(2, profile.Columns.Single(c => c.Column == "Area").DistinctValues);
        }
        finally
        {
            await ExecuteAsync(connectionString, $"DROP TABLE {TableName};");
        }
    }

    [SkippableFact]
    public async Task A_column_the_scan_cannot_test_is_reported_rather_than_failing_the_probe()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var connectionString = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        await ExecuteAsync(connectionString, CreateScript);

        try
        {
            var table = (await new SqlServerSchemaReader(connectionString).ReadAsync())
                .Tables.Single(t => t.Identity.QualifiedName == TableName);

            var profile = await new SqlServerKeyUniquenessChecker(connectionString)
                .ProfileAsync(table, ["Blob"]);

            Assert.False(profile.WasProbed);
            Assert.NotNull(profile.Problem);
            Assert.Empty(profile.Columns);
        }
        finally
        {
            await ExecuteAsync(connectionString, $"DROP TABLE {TableName};");
        }
    }

    [SkippableFact]
    public async Task A_unique_constraint_is_a_key_the_schema_already_states()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var connectionString = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        await ExecuteAsync(connectionString, """
            CREATE TABLE dbo.LedgerProbe (
                Ref     NVARCHAR(20) NOT NULL CONSTRAINT UQ_LedgerProbe_Ref UNIQUE,
                Amount  DECIMAL(18,2) NOT NULL
            );
            INSERT INTO dbo.LedgerProbe (Ref, Amount) VALUES (N'A1', 10), (N'A2', 20);
            """);

        try
        {
            var table = (await new SqlServerSchemaReader(connectionString).ReadAsync())
                .Tables.Single(t => t.Identity.QualifiedName == "dbo.LedgerProbe");

            // The declared key is what the picker offers here; no scan is needed to find it.
            Assert.Null(table.PrimaryKey);
            var declared = Assert.Single(table.UniqueConstraints);
            Assert.Equal("UQ_LedgerProbe_Ref", declared.Name);
            Assert.Equal(["Ref"], declared.Columns.Select(c => c.Name));

            // And it holds when actually checked, which is what the confirming click runs.
            var check = await new SqlServerKeyUniquenessChecker(connectionString)
                .CheckAsync(table, ["Ref"]);

            Assert.True(check.IsUnique);
        }
        finally
        {
            await ExecuteAsync(connectionString, "DROP TABLE dbo.LedgerProbe;");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
