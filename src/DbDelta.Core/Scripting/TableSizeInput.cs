namespace DbDelta.Core.Scripting;

// What is knowable about one picked table before any of its rows have been read.
//
// ExactRows is set only when the table was narrowed to particular rows, which is the one case where the
// count is not a guess. Otherwise the scan gives a yes/no on whether the table differs and the volume
// reader gives the row counts, and the number of *differing* rows sits somewhere between them.
public sealed record TableSizeInput(
    string Table,
    bool Scanned,
    bool Differs,
    long? ExactRows,
    long SourceRows,
    long TargetRows,
    long BytesPerRow);
