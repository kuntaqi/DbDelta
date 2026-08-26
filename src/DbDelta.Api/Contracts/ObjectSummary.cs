namespace DbDelta.Api.Contracts;

public sealed record ObjectSummary(
    string Id,
    string Type,
    string Schema,
    string Name,
    string QualifiedName,
    string Kind,
    string Summary,
    int ChangedChildren);
