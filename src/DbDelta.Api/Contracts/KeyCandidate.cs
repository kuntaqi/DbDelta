namespace DbDelta.Api.Contracts;

public sealed record KeyCandidate(string Column, string DataType, bool Nullable);
