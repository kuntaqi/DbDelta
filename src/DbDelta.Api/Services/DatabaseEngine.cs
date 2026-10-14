namespace DbDelta.Api.Services;

// Normalises the engine a request or a connection names to a provider Key. SQL Server is the default so an
// older client that sends no engine behaves exactly as before, and a handful of common aliases are accepted
// rather than only the canonical key.
public static class DatabaseEngine
{
    public const string SqlServer = "sqlserver";
    public const string PostgreSql = "postgresql";

    public static string Normalize(string? engine) => engine?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sqlserver" or "sqlserver2022" or "mssql" or "sql" => SqlServer,
        "postgresql" or "postgres" or "pg" or "npgsql" => PostgreSql,
        var other => throw new InvalidOperationException(
            $"Unknown database engine '{other}'. Use '{SqlServer}' or '{PostgreSql}'."),
    };
}
