namespace DbDelta.Core.Scripting;

// The rows of one step, carried beside the script instead of inside it. A step with a load is the only
// kind whose SQL is not the whole story: applying it streams these rows into the staging table, and
// downloading it writes them to a data file the script reads with BULK INSERT.
//
// Values stay as text, exactly as every other part of the data path holds them. The staging table is all
// NVARCHAR, so the conversion to real types happens once, server-side, in the statement that reads it —
// the same place and the same way a literal would have been converted.
public sealed record BulkLoad(
    string Table,
    string StagingTable,
    string DataFileName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows)
{
    public int RowCount => Rows.Count;
}
