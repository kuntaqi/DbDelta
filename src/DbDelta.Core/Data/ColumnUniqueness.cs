namespace DbDelta.Core.Data;

public sealed record ColumnUniqueness(string Column, long DistinctValues, long NullRows)
{
    public bool CouldBeKey(long rowCount) => NullRows == 0 && DistinctValues == rowCount && rowCount > 0;
}
