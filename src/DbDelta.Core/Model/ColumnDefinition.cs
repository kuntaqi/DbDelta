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

    // The columns a computed expression reads. Derived from ComputedExpression, so not compared.
    public IReadOnlyList<string> ComputedFrom { get; init; } = [];

    public string? DefaultExpression { get; init; }

    public string? DefaultConstraintName { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => $"{Name} {DataType}{(IsNullable ? " NULL" : " NOT NULL")}";
}
