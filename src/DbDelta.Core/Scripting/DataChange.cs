using DbDelta.Core.Data;

namespace DbDelta.Core.Scripting;

public sealed record DataChange(
    string Key,
    string Display,
    RowClassification Classification,
    IReadOnlyDictionary<string, string?> SourceValues,
    IReadOnlyDictionary<string, string?> KeyValues);
