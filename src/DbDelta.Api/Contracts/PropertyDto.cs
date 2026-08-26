namespace DbDelta.Api.Contracts;

public sealed record PropertyDto(string Property, string? Source, string? Target);
