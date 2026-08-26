namespace DbDelta.Core.Data;

public sealed record KeyUniqueness(long RowCount, long DistinctKeys, long NullKeyRows)
{
    public bool IsUnique => RowCount == DistinctKeys && NullKeyRows == 0;

    public string Explain(string table) => NullKeyRows > 0
        ? $"{table}: {NullKeyRows} row(s) have a NULL in the chosen key, so those rows cannot be addressed."
        : $"{table}: {RowCount} rows but only {DistinctKeys} distinct key values, so the key does not identify a single row.";
}
