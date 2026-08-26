namespace DbDelta.Api.Contracts;

public sealed record FkNode(
    string QualifiedName,
    int Band,
    string State,
    string Note,
    long Rows,
    long Bytes);
