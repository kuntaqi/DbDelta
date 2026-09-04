namespace DbDelta.Core.Model;

public sealed class IndexDefinition
{
    public required string Name { get; init; }

    public IReadOnlyList<IndexColumn> Columns { get; init; } = [];

    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    public bool IsUnique { get; init; }

    public bool IsClustered { get; init; }

    public IndexKind Kind { get; init; } = IndexKind.Rowstore;

    public string? FilterExpression { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;
}
