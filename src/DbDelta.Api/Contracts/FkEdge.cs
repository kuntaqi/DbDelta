namespace DbDelta.Api.Contracts;

public sealed record FkEdge(
    string From,
    string To,
    string Name,
    string Columns,
    bool Nullable,
    bool PartOfCycle);
