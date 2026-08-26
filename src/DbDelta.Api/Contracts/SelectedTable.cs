namespace DbDelta.Api.Contracts;

public sealed record SelectedTable(string Table, string Mode, int TopCount);
