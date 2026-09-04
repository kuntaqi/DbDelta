using DbDelta.Core.Model;

namespace DbDelta.Core.Comparison;

// Whether changing a column from what the target has to what the source has can lose data.
//
// This is type arithmetic, not a query: both types are already known from the comparison, so the verdict
// costs nothing and can be reached before anything is applied. It matters because the server's own
// refusals cover only part of the ground — narrowing a string raises an error, while narrowing DECIMAL
// scale or DATETIME2 precision is a legal conversion that rounds every row without complaint.
public static class NarrowingAnalyzer
{
    // Ranked widest to narrowest. A move down the list cannot be proven safe.
    private static readonly string[] IntegerRank = ["bigint", "int", "smallint", "tinyint"];
    private static readonly string[] FloatRank = ["float", "real"];

    private static readonly HashSet<string> Unicode =
        new(StringComparer.OrdinalIgnoreCase) { "nchar", "nvarchar", "ntext" };

    private static readonly HashSet<string> Ansi =
        new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "text" };

    private static readonly HashSet<string> Binary =
        new(StringComparer.OrdinalIgnoreCase) { "binary", "varbinary", "image" };

    private static readonly HashSet<string> Temporal =
        new(StringComparer.OrdinalIgnoreCase) { "datetime2", "time", "datetimeoffset" };

    public static ColumnNarrowing Analyse(ColumnDefinition target, ColumnDefinition source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var from = target.DataType;
        var to = source.DataType;

        if (from == to)
        {
            return NullabilityOnly(target, source);
        }

        // An alias type hides its own definition behind a name, so nothing can be concluded about width
        // from the reference alone. Say that, rather than guessing either way.
        if (from.IsUserDefined || to.IsUserDefined)
        {
            return new ColumnNarrowing(NarrowingKind.Converts,
                $"{Describe(target)} becomes {to}, and one of the two is a user-defined type whose width "
                + "is not visible here — check the type's own definition before applying.");
        }

        var sameName = string.Equals(from.Name, to.Name, StringComparison.OrdinalIgnoreCase);

        if (sameName)
        {
            return SameTypeNarrowing(target, source, from, to) ?? NullabilityOnly(target, source);
        }

        // Unicode to non-unicode drops whatever does not exist in the target code page, silently.
        if (Unicode.Contains(from.Name) && Ansi.Contains(to.Name))
        {
            return new ColumnNarrowing(NarrowingKind.Narrows,
                $"{Describe(target)} becomes {to}: characters outside the target collation's code page "
                + "are replaced, and the server does not refuse it.");
        }

        var rank = Rank(IntegerRank, from.Name, to.Name) ?? Rank(FloatRank, from.Name, to.Name);
        if (rank is true)
        {
            return new ColumnNarrowing(NarrowingKind.Narrows,
                $"{Describe(target)} becomes {to}, which holds a smaller range — any value outside it "
                + "fails the conversion.");
        }

        if (rank is false)
        {
            return NullabilityOnly(target, source);
        }

        if (Family(from.Name) is { } f && Family(to.Name) is { } t && f == t)
        {
            return NullabilityOnly(target, source);
        }

        return new ColumnNarrowing(NarrowingKind.Converts,
            $"{Describe(target)} becomes {to}, a different kind of type — whether every existing value "
            + "survives depends on the values themselves.");
    }

    private static ColumnNarrowing? SameTypeNarrowing(
        ColumnDefinition target,
        ColumnDefinition source,
        DataTypeSpec from,
        DataTypeSpec to)
    {
        // MAX is unbounded, so leaving it for a fixed size is a narrowing however large that size is.
        if (from.IsMax && !to.IsMax)
        {
            return new ColumnNarrowing(NarrowingKind.Narrows,
                $"{Describe(target)} becomes {to}: anything longer than {to.MaxLength} is truncated.");
        }

        if (from.MaxLength is { } fromLen && to.MaxLength is { } toLen && toLen < fromLen && !to.IsMax)
        {
            var unit = Binary.Contains(from.Name) ? "bytes" : "characters";
            return new ColumnNarrowing(NarrowingKind.Narrows,
                $"{Describe(target)} becomes {to}: existing values longer than {toLen} {unit} do not fit. "
                + "The server refuses this one rather than truncating, so the apply fails instead.");
        }

        // Scale is the dangerous half. Dropping it rounds every row and the server reports success.
        if (from.Scale is { } fromScale && to.Scale is { } toScale && toScale < fromScale)
        {
            return new ColumnNarrowing(NarrowingKind.Narrows,
                $"{Describe(target)} becomes {to}: every value is rounded to {toScale} decimal place(s), "
                + "and the server does not refuse it — this rewrites data while reporting success.");
        }

        if (from.Precision is { } fromPrec && to.Precision is { } toPrec && toPrec < fromPrec)
        {
            // A temporal type carries its fractional-seconds digits in Precision — the reader puts them
            // there so the type renders as DATETIME2(3) rather than DATETIME2(23,3). Losing them rounds
            // the value, and rounds it *up* past a whole second when the fraction is high enough. Nothing
            // overflows, so saying so would send the reader looking for the wrong thing.
            if (Temporal.Contains(from.Name))
            {
                return new ColumnNarrowing(NarrowingKind.Narrows,
                    $"{Describe(target)} becomes {to}: fractional seconds are rounded to {toPrec} digit(s), "
                    + "which can move a timestamp forward, and the server does not refuse it.");
            }

            return new ColumnNarrowing(NarrowingKind.Narrows,
                $"{Describe(target)} becomes {to}: values needing more than {toPrec} digit(s) overflow.");
        }

        return null;
    }

    // Not a narrowing of the type, but NULL to NOT NULL fails outright on any row that holds a NULL.
    private static ColumnNarrowing NullabilityOnly(ColumnDefinition target, ColumnDefinition source)
    {
        if (target.IsNullable && !source.IsNullable)
        {
            return new ColumnNarrowing(NarrowingKind.Converts,
                $"{target.Name} becomes NOT NULL: any existing NULL fails the change, and there is no "
                + "default here to fill it with.");
        }

        return ColumnNarrowing.None;
    }

    // true when the move is narrower, false when wider, null when the pair is not on this ladder.
    private static bool? Rank(string[] ladder, string from, string to)
    {
        var f = Array.FindIndex(ladder, x => string.Equals(x, from, StringComparison.OrdinalIgnoreCase));
        var t = Array.FindIndex(ladder, x => string.Equals(x, to, StringComparison.OrdinalIgnoreCase));
        return f < 0 || t < 0 ? null : t > f;
    }

    private static string? Family(string name) =>
        Unicode.Contains(name) ? "text"
        : Ansi.Contains(name) ? "text"
        : Binary.Contains(name) ? "binary"
        : null;

    private static string Describe(ColumnDefinition column) => $"{column.Name} {column.DataType}";
}
