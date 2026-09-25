namespace DbDelta.SqlServer;

internal static class TableExtras
{
    // Why the table cannot be rebuilt, as clauses joined with "; ". Absent when nothing stops it.
    public const string RebuildBlockers = "Table.RebuildBlockers";
}
