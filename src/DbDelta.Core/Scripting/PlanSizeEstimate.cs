namespace DbDelta.Core.Scripting;

// A range, not a number, and the range is the honest part. Notes carry what could not be established, so
// a caller cannot show the figures without also being able to show why they are approximate.
public sealed record PlanSizeEstimate(
    long SchemaBytes,
    long MinDataBytes,
    long MaxDataBytes,
    long MinRows,
    long MaxRows,
    int TablesCounted,
    int TablesNotScanned,
    ReviewableVerdict Verdict,
    IReadOnlyList<string> Notes)
{
    public long MinBytes => SchemaBytes + MinDataBytes;

    public long MaxBytes => SchemaBytes + MaxDataBytes;

    // Deliberately about the rows and not the bytes. How many rows move can be known exactly — every picked
    // table narrowed to a row list, or scanned and found identical. How wide those rows are as SQL cannot,
    // so a byte figure is a range even when the row count is not.
    public bool RowsAreExact => MinRows == MaxRows;
}
