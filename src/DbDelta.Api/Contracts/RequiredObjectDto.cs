namespace DbDelta.Api.Contracts;

public sealed record RequiredObjectDto(
    string Id,
    string Type,
    string QualifiedName,
    string RequiredBy,
    string Reason);
