using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// Collation was a warning attached to the comparison for a long time: the two database defaults were
// compared, and if they differed the screen said data compare could not be trusted. It stopped nothing,
// it said nothing about which table or column was at risk, and — because it compared names — it fired on
// pairs that behave identically. A warning that is usually wrong and never blocking is read once.
//
// This asks the narrower question instead: for the columns this comparison actually reads, does the
// difference change an answer? Three things can happen, and only two of them matter.
//
//   Code page, on a non-Unicode column. The row hash is built by converting each value to nvarchar, and
//   that conversion decodes the stored bytes through the column's code page. The same byte under 1252 and
//   under 1251 is a different character, so identical bytes hash differently — the compare reports a
//   difference that is not one, and the write it proposes cannot round-trip, because a character the
//   target's code page has no room for is stored as a question mark. Silent at both ends. Blocking.
//
//   Sensitivity, on a key column. The merge join decides "same row" by comparing key text ordinally; the
//   database decides "same row" by its own collation. When one side is case- or accent-insensitive and the
//   other is not, two source rows can be one target row. The compare then reports rows as missing that are
//   not, and the inserts it emits collide with the target's own unique index. Blocking.
//
//   Sensitivity, on any other compared column. The hash is over exact characters, so the comparison stays
//   correct and the write is fine. Worth saying, worth nothing more. Advisory.
//
// A pair whose names differ but whose code page and sensitivity agree produces no finding at all. That is
// the case this check exists to stop reporting.
public static class CollationPrecondition
{
    // Unicode columns hold characters, not bytes in a code page, so nothing decodes and the code page a
    // collation carries is beside the point. Only sensitivity can matter for these.
    private static readonly HashSet<string> UnicodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "nchar", "nvarchar", "ntext", "sysname" };

    private static readonly HashSet<string> NonUnicodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "text" };

    public static IReadOnlyList<CollationFinding> Evaluate(
        TableDefinition source,
        TableDefinition target,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> comparedColumns,
        IReadOnlyDictionary<string, CollationFact> facts)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(comparedColumns);
        ArgumentNullException.ThrowIfNull(facts);

        var keys = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var findings = new List<CollationFinding>();

        foreach (var name in keyColumns.Concat(comparedColumns).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var left = source.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            if (left is null || !targetColumns.TryGetValue(name, out var right))
            {
                continue;
            }

            var finding = Examine(source.Identity, left, right, keys.Contains(name), facts);

            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        return findings;
    }

    // The whole-comparison view, for the summary the compare screen shows. Per-table checks re-run this
    // with the key that was actually chosen; this one uses each table's declared key, because at the
    // moment a comparison is made nobody has chosen anything yet.
    public static IReadOnlyList<CollationFinding> EvaluateSchema(
        DatabaseSchema source,
        DatabaseSchema target,
        IReadOnlyDictionary<string, CollationFact> facts)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(facts);

        var targets = target.Tables.ToDictionary(t => t.Identity);
        var findings = new List<CollationFinding>();

        foreach (var table in source.Tables)
        {
            if (!targets.TryGetValue(table.Identity, out var other))
            {
                continue;
            }

            var key = ColumnSetResolver.DefaultKeyFor(table);
            var columns = ColumnSetResolver.Resolve(
                table, other, new DataCompareRequest { Table = table.Identity, KeyColumns = key });

            findings.AddRange(Evaluate(table, other, key, columns.ComparedColumns, facts));
        }

        return findings;
    }

    public static bool Blocks(IEnumerable<CollationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Any(f => f.Risk == CollationRisk.Blocking);
    }

    private static CollationFinding? Examine(
        ObjectIdentity table,
        ColumnDefinition source,
        ColumnDefinition target,
        bool isKey,
        IReadOnlyDictionary<string, CollationFact> facts)
    {
        // A column with no collation is not textual, whatever its type name says. Reading the collation
        // rather than the type is what makes this hold for an alias type over a string.
        if (source.Collation is null || target.Collation is null)
        {
            return null;
        }

        if (string.Equals(source.Collation, target.Collation, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var left = Fact(facts, source.Collation);
        var right = Fact(facts, target.Collation);

        // Unresolved is treated as risky rather than as fine. The names differ, nothing here can say
        // whether that matters, and the cost of guessing "fine" is silently wrong data.
        if (!left.Resolved || !right.Resolved)
        {
            return new CollationFinding(
                table,
                source.Name,
                CollationRisk.Blocking,
                source.Collation,
                target.Collation,
                "the server could not describe one of these collations, so whether the difference matters "
                + "is unknown");
        }

        if (IsNonUnicode(source) && IsNonUnicode(target) && left.CodePage != right.CodePage)
        {
            return new CollationFinding(
                table,
                source.Name,
                CollationRisk.Blocking,
                source.Collation,
                target.Collation,
                $"code page {left.CodePage} against {right.CodePage}: the same stored bytes are different "
                + "characters on the two sides, so equal rows hash differently, and a value the target's "
                + "code page cannot hold would be written as a question mark");
        }

        if (left.SameSensitivityAs(right))
        {
            return null;
        }

        return isKey
            ? new CollationFinding(
                table,
                source.Name,
                CollationRisk.Blocking,
                source.Collation,
                target.Collation,
                $"a key column that is {left.Sensitivity} on the source and {right.Sensitivity} on the "
                + "target: the two sides do not agree on which rows are the same row")
            : new CollationFinding(
                table,
                source.Name,
                CollationRisk.Advisory,
                source.Collation,
                target.Collation,
                $"{left.Sensitivity} against {right.Sensitivity}. Values are compared exactly, so this "
                + "changes no result here — it is the target's own comparisons that will behave differently");
    }

    // Only the character types decode through a code page. The catalog read resolves an alias type to its
    // underlying type name, so this sees a real type either way.
    private static bool IsNonUnicode(ColumnDefinition column) =>
        NonUnicodeTypes.Contains(column.DataType.Name)
        || (!UnicodeTypes.Contains(column.DataType.Name)
            && !column.DataType.Name.StartsWith('N') && !column.DataType.Name.StartsWith('n'));

    private static CollationFact Fact(IReadOnlyDictionary<string, CollationFact> facts, string name) =>
        facts.TryGetValue(name, out var fact) ? fact : CollationFact.Unresolved(name);
}
