namespace DbDelta.Api.Contracts;

// PickedRows is null for a whole-table selection. The screen needs the keys themselves, not just how
// many, because it has to show which of the rows in front of you are the ones that are in.
public sealed record SelectedTable(
    string Table,
    string Mode,
    int TopCount,
    IReadOnlyList<string>? PickedRows = null);
