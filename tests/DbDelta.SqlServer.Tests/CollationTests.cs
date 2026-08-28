using DbDelta.Core.Data;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// Two halves. The first checks that the reader gets the facts right, because everything downstream is a
// decision made from those numbers. The second checks that the decision is guarding something real: the
// hash the data compare depends on genuinely does differ for identical bytes under two code pages, and
// genuinely does not differ for case sensitivity. Without that half the rule is only an assertion that
// the rule is what it is.
[Collection(nameof(LocalDbCollection))]
public sealed class CollationTests
{
    private readonly LocalDbFixture _fixture;

    public CollationTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_reader_resolves_code_page_and_sensitivity()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var facts = await new SqlServerCollationFactReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase))
            .ReadAsync(["Latin1_General_CI_AS", "SQL_Latin1_General_CP1_CI_AS", "Latin1_General_CS_AS", "Cyrillic_General_CI_AS"]);

        Assert.Equal(1252, facts["Latin1_General_CI_AS"].CodePage);
        Assert.Equal(1252, facts["SQL_Latin1_General_CP1_CI_AS"].CodePage);
        Assert.Equal(1251, facts["Cyrillic_General_CI_AS"].CodePage);

        Assert.True(facts["Latin1_General_CI_AS"].IgnoresCase);
        Assert.False(facts["Latin1_General_CS_AS"].IgnoresCase);

        // The pair that made the old warning worthless: different names, identical behaviour.
        Assert.True(facts["Latin1_General_CI_AS"].SameSensitivityAs(facts["SQL_Latin1_General_CP1_CI_AS"]));
    }

    // Unresolved has to be distinguishable from resolved, because the precondition treats it as risky and
    // an empty result that looked like agreement would do the opposite.
    [SkippableFact]
    public async Task A_name_the_server_does_not_know_comes_back_unresolved()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var facts = await new SqlServerCollationFactReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase))
            .ReadAsync(["Not_A_Real_Collation"]);

        Assert.False(facts["Not_A_Real_Collation"].Resolved);
    }

    [SkippableFact]
    public async Task Nothing_is_asked_of_the_server_for_an_empty_list()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var facts = await new SqlServerCollationFactReader("Server=nowhere;Connect Timeout=1")
            .ReadAsync([]);

        Assert.Empty(facts);
    }

    // The reason the code page rule exists, proved rather than asserted. The same byte in two columns that
    // differ only by code page hashes differently under the tool's own digest expression — so a data
    // compare across such a pair reports rows as changed when the stored bytes are identical, and the
    // update it proposes writes a character the target cannot hold.
    [SkippableFact]
    public async Task Identical_bytes_hash_differently_across_code_pages_but_not_across_case_sensitivity()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "Collation";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            await ExecuteAsync(connectionString, """
                CREATE TABLE dbo.Coll (
                    Latin      VARCHAR(20) COLLATE Latin1_General_CI_AS,
                    Cyrillic   VARCHAR(20) COLLATE Cyrillic_General_CI_AS,
                    LatinCased VARCHAR(20) COLLATE Latin1_General_CS_AS
                );
                INSERT dbo.Coll (Latin, Cyrillic, LatinCased)
                VALUES (CONVERT(VARCHAR(20), 0xE0), CONVERT(VARCHAR(20), 0xE0), CONVERT(VARCHAR(20), 0xE0));
                """);

            // The same expression the row hash reader builds, so this measures the tool rather than a
            // hand-written approximation of it.
            var differsByCodePage = await ScalarAsync(connectionString, """
                SELECT CASE WHEN HASHBYTES('SHA2_256', CONVERT(NVARCHAR(MAX), Latin))
                               = HASHBYTES('SHA2_256', CONVERT(NVARCHAR(MAX), Cyrillic))
                            THEN 0 ELSE 1 END
                FROM dbo.Coll;
                """);

            var differsByCase = await ScalarAsync(connectionString, """
                SELECT CASE WHEN HASHBYTES('SHA2_256', CONVERT(NVARCHAR(MAX), Latin))
                               = HASHBYTES('SHA2_256', CONVERT(NVARCHAR(MAX), LatinCased))
                            THEN 0 ELSE 1 END
                FROM dbo.Coll;
                """);

            Assert.Equal(1, differsByCodePage);
            Assert.Equal(0, differsByCase);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
