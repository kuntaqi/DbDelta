namespace DbDelta.Core.Model;

public sealed class UserDefinedTypeDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required UserDefinedTypeKind Kind { get; init; }

    // Alias types only: the built-in type behind the name, and whether the name itself allows NULL.
    public DataTypeSpec? BaseType { get; init; }

    public bool IsNullable { get; init; }

    // Table types only.
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => $"{Kind} {Identity.QualifiedName}";
}
