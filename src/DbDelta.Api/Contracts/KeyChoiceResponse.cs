namespace DbDelta.Api.Contracts;

public sealed record KeyChoiceResponse(
    string Table,
    IReadOnlyList<string> Chosen,
    bool FromPrimaryKey,
    IReadOnlyList<KeyCandidate> Candidates,
    string? Problem);
