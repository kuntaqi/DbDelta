namespace DbDelta.Core.Model;

// IsUserDefined changes what the name means. A built-in carries its size with it; an alias type is a
// name that already includes one, so writing NVARCHAR(20) and writing dbo.PhoneNumber are different
// statements even when the second is defined as the first.
public sealed record DataTypeSpec(
    string Name,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    bool IsMax = false,
    bool IsUserDefined = false,
    string? Schema = null)
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public bool Equals(DataTypeSpec? other) =>
        other is not null
        && Comparer.Equals(Name, other.Name)
        && MaxLength == other.MaxLength
        && Precision == other.Precision
        && Scale == other.Scale
        && IsMax == other.IsMax
        && IsUserDefined == other.IsUserDefined
        && Comparer.Equals(Schema ?? string.Empty, other.Schema ?? string.Empty);

    public override int GetHashCode() =>
        HashCode.Combine(Comparer.GetHashCode(Name), MaxLength, Precision, Scale, IsMax, IsUserDefined);

    public override string ToString()
    {
        // A user-defined type is named, not sized: its length is part of the definition, not of the
        // reference to it. Rendering it unquoted keeps this type engine-neutral; the emitter quotes.
        if (IsUserDefined)
        {
            return Schema is null ? Name : $"{Schema}.{Name}";
        }

        if (IsMax)
        {
            return $"{Name}(MAX)";
        }

        if (Precision is not null)
        {
            return Scale is not null ? $"{Name}({Precision},{Scale})" : $"{Name}({Precision})";
        }

        return MaxLength is not null ? $"{Name}({MaxLength})" : Name;
    }
}
