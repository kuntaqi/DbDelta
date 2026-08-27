namespace DbDelta.Api.Contracts;

public sealed record InstanceSurveyResponse(
    string Server,
    EnvironmentClass Environment,
    bool ReadOnly,
    IReadOnlyList<InstanceDatabaseDto> Databases,
    string? Warning);
