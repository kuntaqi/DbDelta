using System.Net;
using System.Net.Http.Json;
using DbDelta.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DbDelta.Api.Tests;

// The capstone proof that the provider-selection wiring works end to end: a real HTTP request carrying
// engine="postgresql" reaches a PostgreSQL database through the whole app. Needs a live server, so it skips when
// DBDELTA_PG_TEST is unset. Everything below the HTTP boundary — the factory, the connection factory, the PG
// provider — is what actually gets exercised.
[Collection("pg-api")]
[Trait("RequiresPostgres", "true")]
public sealed class PostgreSqlApiReachabilityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PostgreSqlApiReachabilityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [SkippableFact]
    public async Task Probe_reaches_postgres_through_the_api()
    {
        var connection = PostgresRequest(Database(out var database));
        Skip.If(database is null, "DBDELTA_PG_TEST is not set.");

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/probe", connection);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(database!, body, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Compare_reaches_postgres_through_the_api()
    {
        var connection = PostgresRequest(Database(out var database));
        Skip.If(database is null, "DBDELTA_PG_TEST is not set.");

        using var client = _factory.CreateClient();
        // Comparing the database against itself proves the whole HTTP -> CompareService -> PostgreSQL path; the
        // diff is naturally empty.
        var response = await client.PostAsJsonAsync("/api/compare", new CompareRequest(connection, connection));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_missing_postgres_database_is_reported_not_crashed()
    {
        Skip.If(Database(out _) is null, "DBDELTA_PG_TEST is not set.");
        var connection = PostgresRequest("dbdelta_does_not_exist");

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/probe", connection);

        // The provider-neutral error mapper turns PostgreSQL SQLSTATE 3D000 into a clean 400, not a 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ConnectionRequest PostgresRequest(string? database)
    {
        var builder = Builder();
        return new ConnectionRequest(
            Engine: "postgresql",
            Server: builder?.Host ?? "127.0.0.1",
            Port: builder is { Port: > 0 } ? builder.Port : 5432,
            Database: database,
            Username: builder?.Username,
            Password: builder?.Password);
    }

    private static string? Database(out string? database)
    {
        database = Builder()?.Database;
        return database;
    }

    private static NpgsqlConnectionStringBuilder? Builder()
    {
        var connectionString = Environment.GetEnvironmentVariable("DBDELTA_PG_TEST");
        return string.IsNullOrWhiteSpace(connectionString) ? null : new NpgsqlConnectionStringBuilder(connectionString);
    }
}

[CollectionDefinition("pg-api")]
public sealed class PgApiCollection;
