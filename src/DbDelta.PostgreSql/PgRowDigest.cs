namespace DbDelta.PostgreSql;

// The server-side digest the whole data path turns on, mirroring the SQL Server RowDigestBuilder. Each column
// becomes "|<len>:<text>" — length-prefixed so ('a','bc') and ('ab','c') cannot collapse, and NULL-tagged
// ("|-:") so a NULL is distinct from an empty string ("|0:"). Every value is rendered with ::text and compared
// as text, which is the same terms the key, the row detail and the parent closure all use.
//
// Both sides of a compare are PostgreSQL read by this same code, so the digest only has to be internally
// deterministic, not match any other engine — which is why md5 (built in, no pgcrypto extension) is enough:
// this is change detection, not cryptography.
internal static class PgRowDigest
{
    private static readonly PostgreSqlQuoter Q = PostgreSqlQuoter.Instance;

    public static string Concatenation(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return "''";
        }

        return string.Join(" || ", columns.Select(c =>
        {
            var text = $"({Q.Quote(c)})::text";
            return $"'|' || coalesce(length({text})::text, '-') || ':' || coalesce({text}, '')";
        }));
    }

    public static string Hash(IReadOnlyList<string> columns) => $"md5({Concatenation(columns)})";
}
