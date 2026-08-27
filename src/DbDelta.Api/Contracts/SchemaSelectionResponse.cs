namespace DbDelta.Api.Contracts;

public sealed record SchemaSelectionResponse(
    IReadOnlyList<string> Selected,
    int Differing,
    int DataTables,
    IReadOnlyList<RequiredObjectDto> Required,
    IReadOnlyList<string> Unsatisfiable,
    string Scope,
    IReadOnlyList<ExcludedObjectDto> Excluded);
