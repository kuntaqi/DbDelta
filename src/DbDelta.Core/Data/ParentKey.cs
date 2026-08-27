namespace DbDelta.Core.Data;

// One parent row's key, as the text the rest of the data path already deals in. Comparing as text is
// what keeps a child's foreign key value and the parent's key value in the same terms: both arrive
// through the same reader conversion, so an INT 5 is "5" on either side of the reference.
public sealed record ParentKey(IReadOnlyList<string?> Values)
{
    // Length-prefixed like the server-side row digest, and for the same reason: without it ("a", "bc")
    // and ("ab", "c") collapse to the same text and two different parents look like one.
    public string Canonical =>
        string.Concat(Values.Select(v => v is null ? "|-" : $"|{v.Length}:{v}"));

    public bool HasNull => Values.Any(v => v is null);

    public string Display => string.Join(", ", Values.Select(v => v ?? "NULL"));
}
