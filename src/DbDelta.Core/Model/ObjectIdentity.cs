namespace DbDelta.Core.Model;

// Equality is case-insensitive because SQL Server's default collation is, and this type is the dedup
// key for change units. Ordinal equality here would let dbo.Company and dbo.COMPANY emit twice.
public sealed record ObjectIdentity(ObjectType Type, string Schema, string Name)
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public string QualifiedName => $"{Schema}.{Name}";

    public bool Equals(ObjectIdentity? other) =>
        other is not null
        && Type == other.Type
        && Comparer.Equals(Schema, other.Schema)
        && Comparer.Equals(Name, other.Name);

    public override int GetHashCode() =>
        HashCode.Combine(Type, Comparer.GetHashCode(Schema), Comparer.GetHashCode(Name));

    public override string ToString() => $"{Type} {QualifiedName}";
}
