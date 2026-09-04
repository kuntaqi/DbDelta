namespace DbDelta.SqlServer.Tests;

// The queries are the risky part here, and one of them already surprised me: sys.databases.collation_name
// reads NULL on this instance for every database, and DATABASEPROPERTYEX returns NULL when given a column
// rather than a literal. That is why collation costs a connection, and why these run against a real server.
//
// Assertions are about containment, never about the whole list: the instance a developer runs this on has
// databases that are none of this repo's business.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollection))]
public sealed class InstanceSurveyTests
{
    private readonly LocalDbFixture _fixture;

    public InstanceSurveyTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_instance_lists_its_user_databases_with_sizes()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var databases = await LocalDbFixture.WithQuietInstanceAsync(() => new SqlServerProvider()
            .ListDatabasesAsync(LocalDbFixture.ConnectionStringFor("master")));

        var source = databases.Single(d => d.Name == LocalDbFixture.SourceDatabase);

        Assert.Equal("ONLINE", source.State);
        Assert.True(source.DataBytes > 0, "a database that exists has files");
        Assert.True(source.LogBytes > 0, "and a log");
        Assert.False(source.IsReadOnly);
        Assert.True(source.Accessible);
        Assert.Contains(databases, d => d.Name == LocalDbFixture.TargetDatabase);
    }

    // None of them is something this tool would ever sync, and leaving them in would put tempdb at the top
    // of a list of sync candidates.
    [SkippableFact]
    public async Task System_databases_are_left_out()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var databases = await LocalDbFixture.WithQuietInstanceAsync(() => new SqlServerProvider()
            .ListDatabasesAsync(LocalDbFixture.ConnectionStringFor("master")));

        Assert.DoesNotContain(databases, d =>
            d.Name is "master" or "model" or "msdb" or "tempdb");
    }

    // The half that costs a connection. Counts and collation both come from inside the database, which is
    // the whole reason the survey is two passes.
    [SkippableFact]
    public async Task Describing_one_database_reads_its_collation_and_object_counts()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var detail = await LocalDbFixture.WithQuietInstanceAsync(() => new SqlServerProvider()
            .DescribeDatabaseAsync(LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)));

        // Checked against the schema reader rather than against numbers written here: two different queries
        // agreeing is worth testing, and a hardcoded count only records what the fixture happened to hold.
        var schema = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();

        Assert.Null(detail.Problem);
        Assert.Equal(schema.Collation, detail.Collation);
        Assert.Equal(schema.Tables.Count, detail.Tables);
        Assert.Equal(schema.Views.Count, detail.Views);
        Assert.Equal(schema.Routines.Count, detail.Routines);
    }

    // A database that cannot be opened is a fact about the instance, so it is reported rather than dropped
    // from the list or allowed to take the survey down.
    [SkippableFact]
    public async Task A_database_that_cannot_be_opened_reports_why_instead_of_throwing()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        // A short timeout and no connect retry, which is worth spelling out: the shared connection string
        // carries a 30-second timeout and SqlClient retries a failed connect once after ten seconds, so
        // this one test used to spend about ten seconds proving a connection fails. What is under test is
        // the reporting, not the waiting.
        var detail = await new SqlServerProvider().DescribeDatabaseAsync(
            @"Server=(localdb)\MSSQLLocalDB;Database=DbDelta_NoSuchDatabase;Integrated Security=true;"
            + "TrustServerCertificate=true;Connect Timeout=5;ConnectRetryCount=0");

        Assert.NotNull(detail.Problem);
        Assert.Equal("DbDelta_NoSuchDatabase", detail.Name);
        Assert.Equal(0, detail.Tables);
    }
}
