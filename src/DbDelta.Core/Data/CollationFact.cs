namespace DbDelta.Core.Data;

// What a collation name actually means, asked of the server rather than parsed out of the name.
//
// The distinction earns its keep immediately: Latin1_General_CI_AS and SQL_Latin1_General_CP1_CI_AS are
// different names for the same code page and the same sensitivity, and a check that compares names calls
// that pair dangerous. It is the commonest mismatch there is, so a name comparison spends its credibility
// on the one case that does not matter and has nothing left for the case that does.
public sealed record CollationFact(string Name, int? CodePage, int? ComparisonStyle)
{
    // SQL Server reports these as a bitmask. Read from the server, not inferred from an _CI_AS suffix:
    // the suffix is a convention, and a collation that does not follow it would be read wrongly with
    // complete confidence.
    private const int IgnoreCase = 0x1;
    private const int IgnoreAccent = 0x2;
    private const int IgnoreKana = 0x10000;
    private const int IgnoreWidth = 0x20000;

    public static CollationFact Unresolved(string name) => new(name, null, null);

    public bool Resolved => CodePage.HasValue && ComparisonStyle.HasValue;

    public bool IgnoresCase => Has(IgnoreCase);

    public bool IgnoresAccent => Has(IgnoreAccent);

    public bool IgnoresKana => Has(IgnoreKana);

    public bool IgnoresWidth => Has(IgnoreWidth);

    // Two collations agree on what counts as the same string when all four bits agree. Comparing the
    // whole mask rather than the bits one at a time keeps any future bit from being silently ignored.
    public bool SameSensitivityAs(CollationFact other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ComparisonStyle == other.ComparisonStyle;
    }

    public string Sensitivity =>
        ComparisonStyle is null
            ? "unknown"
            : string.Join(
                "/",
                new[]
                {
                    IgnoresCase ? "case-insensitive" : "case-sensitive",
                    IgnoresAccent ? "accent-insensitive" : "accent-sensitive"
                });

    private bool Has(int bit) => ComparisonStyle is { } style && (style & bit) != 0;
}
