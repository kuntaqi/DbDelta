namespace DbDelta.Api.Contracts;

public sealed record DataSelectionResponse(IReadOnlyList<SelectedTable> Selected);
