using Npgsql;

namespace DbDelta.PostgreSql.Tests;

// The reader test needs a live PostgreSQL, which this machine cannot host for everyone, so the fixture reads a
// connection string from an environment variable and probes it. When it is unset or unreachable the tests skip
// rather than fail — the same posture the SQL Server suite takes toward LocalDB, inverted: PostgreSQL runs in
// Linux CI and is absent on Windows and most clones. Point DBDELTA_PG_TEST at a throwaway database.
public sealed class PgFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DBDELTA_PG_TEST";

    public string? ConnectionString { get; private set; }

    public bool Available { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            UnavailableReason = $"{EnvironmentVariable} is not set.";
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or ArgumentException or InvalidOperationException)
        {
            UnavailableReason = ex.Message;
            return;
        }

        ConnectionString = connectionString;
        Available = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(nameof(PgCollection))]
public sealed class PgCollection : ICollectionFixture<PgFixture>;
