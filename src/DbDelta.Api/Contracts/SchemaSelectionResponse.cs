namespace DbDelta.Api.Contracts;

public sealed record SchemaSelectionResponse(
    IReadOnlyList<string> Selected,
    int Differing,
    int DataTables);
