namespace DbDelta.Core.Model;

public sealed record DataTypeSpec(
    string Name,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    bool IsMax = false)
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public bool Equals(DataTypeSpec? other) =>
        other is not null
        && Comparer.Equals(Name, other.Name)
        && MaxLength == other.MaxLength
        && Precision == other.Precision
        && Scale == other.Scale
        && IsMax == other.IsMax;

    public override int GetHashCode() =>
        HashCode.Combine(Comparer.GetHashCode(Name), MaxLength, Precision, Scale, IsMax);

    public override string ToString()
    {
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
