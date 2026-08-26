namespace DbDelta.Core.Model;

public sealed class TableDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    public PrimaryKeyDefinition? PrimaryKey { get; init; }

    public IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints { get; init; } = [];

    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];

    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; init; } = [];

    public IReadOnlyList<CheckConstraintDefinition> CheckConstraints { get; init; } = [];

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => Identity.QualifiedName;
}
