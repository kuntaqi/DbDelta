namespace DbDelta.Api.Contracts;

public sealed record ProbeResponse(
    string Server,
    string Database,
    string ProductVersion,
    string Edition,
    string Collation,
    EnvironmentClass Environment,
    bool ReadOnly,
    int TableCount,
    int ViewCount,
    int RoutineCount);
