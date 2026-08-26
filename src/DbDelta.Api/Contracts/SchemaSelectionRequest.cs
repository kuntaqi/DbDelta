namespace DbDelta.Api.Contracts;

public sealed record SchemaSelectionRequest(string ObjectId, bool Selected);
