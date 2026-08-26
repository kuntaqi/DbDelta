namespace DbDelta.Api.Contracts;

public sealed record CellDiff(string Column, string? Source, string? Target);
