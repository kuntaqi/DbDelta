namespace DbDelta.Api.Contracts;

public sealed record FkMapResponse(
    string Focus,
    int Depth,
    string Direction,
    IReadOnlyList<FkNode> Nodes,
    IReadOnlyList<FkEdge> Edges,
    IReadOnlyList<string> Cycles,
    IReadOnlyList<string> Notes);
