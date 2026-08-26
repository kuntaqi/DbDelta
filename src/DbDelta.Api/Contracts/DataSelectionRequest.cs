namespace DbDelta.Api.Contracts;

public sealed record DataSelectionRequest(
    string Table,
    bool Selected,
    string Mode = "AllRows",
    int TopCount = 100,
    string? Filter = null);
