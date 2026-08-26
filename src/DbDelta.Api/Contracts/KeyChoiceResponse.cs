namespace DbDelta.Api.Contracts;

public sealed record KeyChoiceResponse(
    string Table,
    IReadOnlyList<string> Chosen,
    bool FromPrimaryKey,
    IReadOnlyList<string> Recommended,
    long RowCount,
    bool Probed,
    IReadOnlyList<KeyCandidate> Candidates,
    string? Problem)
{
    // Set only when a pick was actually refused. Advice about what to pick is not a problem, and the two
    // read very differently to whoever is looking at the screen.
    public bool Rejected { get; init; }
}
