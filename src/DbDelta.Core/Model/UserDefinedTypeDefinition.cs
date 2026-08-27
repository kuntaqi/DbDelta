namespace DbDelta.Core.Model;

public sealed class UserDefinedTypeDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required UserDefinedTypeKind Kind { get; init; }

    // Alias types only: the built-in type behind the name, and whether the name itself allows NULL.
    public DataTypeSpec? BaseType { get; init; }

    public bool IsNullable { get; init; }

    // Table types only. A table type is a table shape, so it carries most of what a table does — but never
    // a foreign key, which T-SQL does not allow inside one.
    //
    // Every constraint name here is system-generated with a random suffix, because "CONSTRAINT name" is a
    // syntax error inside CREATE TYPE AS TABLE. They are read for completeness and deliberately left out of
    // the comparison: matching on them would report two identical types as different. A standalone index is
    // the exception — that syntax does take a name, so its name is real and is compared.
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    public PrimaryKeyDefinition? PrimaryKey { get; init; }

    public IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints { get; init; } = [];

    public IReadOnlyList<CheckConstraintDefinition> CheckConstraints { get; init; } = [];

    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => $"{Kind} {Identity.QualifiedName}";
}
