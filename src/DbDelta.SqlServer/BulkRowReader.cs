using System.Data;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer;

// SqlBulkCopy reads from an IDataReader, and the rows are already in memory as text. Wrapping them is a
// few lines and copies nothing; a DataTable would hold a second copy of a payload that is large by
// definition — being large is the whole reason this path exists.
//
// Every column is a string. The staging table is all NVARCHAR, so there is nothing to convert here.
internal sealed class BulkRowReader : IDataReader
{
    private readonly BulkLoad _load;
    private int _index = -1;

    public BulkRowReader(BulkLoad load) => _load = load;

    public int FieldCount => _load.Columns.Count;

    public bool Read()
    {
        _index++;
        return _index < _load.Rows.Count;
    }

    public object GetValue(int i) => (object?)_load.Rows[_index][i] ?? DBNull.Value;

    public bool IsDBNull(int i) => _load.Rows[_index][i] is null;

    public string GetName(int i) => _load.Columns[i];

    public int GetOrdinal(string name)
    {
        for (var i = 0; i < _load.Columns.Count; i++)
        {
            if (string.Equals(_load.Columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException(name);
    }

    public Type GetFieldType(int i) => typeof(string);

    public string GetDataTypeName(int i) => "nvarchar";

    public object this[int i] => GetValue(i);

    public object this[string name] => GetValue(GetOrdinal(name));

    public int Depth => 0;

    public bool IsClosed => false;

    public int RecordsAffected => -1;

    public void Close()
    {
    }

    public void Dispose()
    {
    }

    public bool NextResult() => false;

    public int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    // SqlBulkCopy asks for values through GetValue and never these, so they exist to satisfy the
    // interface rather than to be used.
    public bool GetBoolean(int i) => throw new NotSupportedException();

    public byte GetByte(int i) => throw new NotSupportedException();

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) =>
        throw new NotSupportedException();

    public char GetChar(int i) => throw new NotSupportedException();

    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) =>
        throw new NotSupportedException();

    public IDataReader GetData(int i) => throw new NotSupportedException();

    public DateTime GetDateTime(int i) => throw new NotSupportedException();

    public decimal GetDecimal(int i) => throw new NotSupportedException();

    public double GetDouble(int i) => throw new NotSupportedException();

    public float GetFloat(int i) => throw new NotSupportedException();

    public Guid GetGuid(int i) => throw new NotSupportedException();

    public short GetInt16(int i) => throw new NotSupportedException();

    public int GetInt32(int i) => throw new NotSupportedException();

    public long GetInt64(int i) => throw new NotSupportedException();

    public string GetString(int i) => (string)GetValue(i);

    public DataTable? GetSchemaTable() => null;
}
