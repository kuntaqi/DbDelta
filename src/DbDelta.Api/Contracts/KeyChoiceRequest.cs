namespace DbDelta.Api.Contracts;

public sealed record KeyChoiceRequest(string Table, IReadOnlyList<string> Columns);
