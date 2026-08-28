namespace DbDelta.Api.Contracts;

// Rows is absent for a whole-table pick and present for a narrowing. Distinguishing "not sent" from
// "sent empty" is the whole of it, so this is a nullable list rather than one that defaults to empty.
public sealed record DataSelectionRequest(
    string Table,
    bool Selected,
    string Mode = "AllRows",
    int TopCount = 100,
    string? Filter = null,
    IReadOnlyList<string>? Rows = null);
