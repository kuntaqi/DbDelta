namespace DbDelta.Api.Contracts;

public sealed record StepDto(string Phase, string Description, string Sql, bool Destructive);
