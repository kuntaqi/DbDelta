using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

// Index-level facts arrive in one query and the per-column facts (order, descending, included) in another, so
// an index is gathered here across both before it becomes an immutable IndexDefinition.
internal sealed class PgIndexAccumulator
{
    public required ObjectIdentity Table { get; init; }

    public required string Name { get; init; }

    public required bool IsUnique { get; init; }

    public required IndexKind Kind { get; init; }

    public string? FilterExpression { get; init; }

    public required string FillFactor { get; init; }

    public List<IndexColumn> KeyColumns { get; } = [];

    public List<string> IncludedColumns { get; } = [];

    public IndexDefinition Build() => new()
    {
        Name = Name,
        Columns = KeyColumns,
        IncludedColumns = IncludedColumns,
        IsUnique = IsUnique,
        // PostgreSQL has no clustered index in the SQL Server sense; uniformly false avoids phantom diffs.
        IsClustered = false,
        Kind = Kind,
        FilterExpression = FilterExpression,
        Extras = new ProviderExtras(new Dictionary<string, string?> { ["FillFactor"] = FillFactor }),
    };
}
