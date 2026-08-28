namespace DbDelta.Core.Data;

// Narrowing a table that is already in the plan down to particular rows.
//
// The shape of this is decided by something the screen does: it shows the first 200 differences and no
// more, because the two-pass design fetches only what is displayed. So a pick can only ever be made from
// rows that were on screen, and a table with five thousand differences cannot be picked through row by
// row. That makes row selection a *narrowing* of a table already chosen, never an alternative way of
// choosing one — and it makes "how many of how many" a number the plan has to keep saying out loud, or a
// person who ticked two hundred rows will believe they dealt with five thousand.
//
// A pick is a key, and keys are re-derived from a fresh comparison every time a script is built. So a
// picked row can simply not be there any more: someone fixed it, or it fell outside a Top N window, or it
// was deleted on the source. Dropping those quietly would make the script smaller than the review that
// approved it, with nothing said. They come back as unmatched instead.
public static class RowPicks
{
    public static RowPickResolution Apply(
        IReadOnlyList<RowDifference> differences,
        IReadOnlySet<string>? picked)
    {
        ArgumentNullException.ThrowIfNull(differences);

        // No picks at all means the whole table, which is what selecting a table has always meant. Null
        // and empty are deliberately different: an empty pick set is a table narrowed to nothing, and
        // treating that as "everything" would write the entire table off the back of unticking its last
        // row.
        if (picked is null)
        {
            return new RowPickResolution
            {
                Kept = differences,
                Unmatched = [],
                AvailableCount = differences.Count,
                IsNarrowed = false
            };
        }

        var kept = differences.Where(d => picked.Contains(d.Key)).ToList();
        var found = kept.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

        return new RowPickResolution
        {
            Kept = kept,
            Unmatched = picked.Where(k => !found.Contains(k)).Order(StringComparer.Ordinal).ToList(),
            AvailableCount = differences.Count,
            IsNarrowed = true
        };
    }
}
