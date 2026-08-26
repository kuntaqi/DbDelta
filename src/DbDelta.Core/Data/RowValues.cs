namespace DbDelta.Core.Data;

public sealed record RowValues(string Key, IReadOnlyDictionary<string, string?> Values);
