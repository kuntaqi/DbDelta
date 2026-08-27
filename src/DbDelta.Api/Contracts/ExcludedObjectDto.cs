namespace DbDelta.Api.Contracts;

public sealed record ExcludedObjectDto(
    string Id,
    string QualifiedName,
    string Kind,
    string Reason);
