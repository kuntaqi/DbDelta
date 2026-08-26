namespace DbDelta.Core.Model;

public sealed class PrimaryKeyDefinition
{
    public required string Name { get; init; }

    public IReadOnlyList<IndexColumn> Columns { get; init; } = [];

    public bool IsClustered { get; init; } = true;
}
