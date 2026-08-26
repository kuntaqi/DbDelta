namespace DbDelta.Core.Model;

public sealed class ColumnDefinition
{
    public required string Name { get; init; }

    public required DataTypeSpec DataType { get; init; }

    public bool IsNullable { get; init; }

    public int OrdinalPosition { get; init; }

    public string? Collation { get; init; }

    public IdentitySpec? Identity { get; init; }

    public string? ComputedExpression { get; init; }

    public string? DefaultExpression { get; init; }

    public string? DefaultConstraintName { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => $"{Name} {DataType}{(IsNullable ? " NULL" : " NOT NULL")}";
}
