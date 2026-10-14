using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

// A user-defined type arrives as a header row (kind, domain base type and nullability) and, for composites and
// enums, its columns or labels from a second query. It is gathered here before becoming an immutable definition.
internal sealed class PgUserDefinedTypeParts
{
    public required ObjectIdentity Identity { get; init; }

    public required UserDefinedTypeKind Kind { get; init; }

    public DataTypeSpec? BaseType { get; init; }

    public bool IsNullable { get; init; }

    public List<ColumnDefinition> Columns { get; } = [];

    public List<string> EnumLabels { get; } = [];

    public List<CheckConstraintDefinition> CheckConstraints { get; } = [];

    public UserDefinedTypeDefinition Build() => new()
    {
        Identity = Identity,
        Kind = Kind,
        BaseType = BaseType,
        IsNullable = IsNullable,
        Columns = Columns,
        EnumLabels = EnumLabels,
        CheckConstraints = CheckConstraints,
    };
}
