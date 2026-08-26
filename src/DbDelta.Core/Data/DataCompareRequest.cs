using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

public sealed class DataCompareRequest
{
    public required ObjectIdentity Table { get; init; }

    public required IReadOnlyList<string> KeyColumns { get; init; }

    public TableDataMode Mode { get; init; } = TableDataMode.AllRows;

    public int TopCount { get; init; } = 100;

    public string? FilterPredicate { get; init; }

    public IReadOnlySet<string> IgnoredColumns { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool HasKey => KeyColumns.Count > 0;
}
