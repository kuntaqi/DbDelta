using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Data;

// One table's planned rows while parent closure is still adding to them. TableDataChanges is what the
// emitter consumes and is built once at the end; this is the working copy in between.
internal sealed class TableState
{
    private readonly List<DataChange> _changes;
    private readonly HashSet<string> _keys;
    private readonly Dictionary<string, HashSet<string>> _byColumns = new(StringComparer.OrdinalIgnoreCase);

    public TableState(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> columns,
        IReadOnlyList<DataChange> changes,
        long targetRowCount = 0)
    {
        Table = table;
        KeyColumns = keyColumns;
        Columns = columns;
        TargetRowCount = targetRowCount;

        _changes = changes.ToList();
        _keys = changes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
    }

    public TableDefinition Table { get; }

    public IReadOnlyList<string> KeyColumns { get; }

    public IReadOnlyList<string> Columns { get; }

    public long TargetRowCount { get; }

    // What an inserted row actually carries, which is what a foreign key can be read out of.
    public IReadOnlyList<string> AllColumns =>
        KeyColumns.Concat(Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static TableState From(TableDataChanges changes) =>
        new(changes.Table, changes.KeyColumns, changes.Columns, changes.Changes, changes.TargetRowCount);

    // Is a row with these values already going in? Asked before fetching, so a parent referenced by two
    // different children is pulled in once.
    public bool Holds(IReadOnlyList<string> columns, ParentKey key)
    {
        var signature = string.Join(",", columns);

        if (!_byColumns.TryGetValue(signature, out var index))
        {
            index = _changes
                .Where(c => c.Classification == RowClassification.Insert)
                .Select(c => new ParentKey(columns.Select(col => c.SourceValues.GetValueOrDefault(col)).ToList()))
                .Select(k => k.Canonical)
                .ToHashSet(StringComparer.Ordinal);

            _byColumns[signature] = index;
        }

        return index.Contains(key.Canonical);
    }

    public DataChange Insert(RowValues row)
    {
        var keyValues = KeyColumns.ToDictionary(
            c => c, c => row.Values.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase);

        var key = new ParentKey(KeyColumns.Select(c => row.Values.GetValueOrDefault(c)).ToList());

        return new DataChange(
            key.Canonical,
            key.Display,
            RowClassification.Insert,
            row.Values,
            keyValues);
    }

    public void Add(IEnumerable<DataChange> rows)
    {
        foreach (var row in rows.Where(r => _keys.Add(r.Key)))
        {
            _changes.Add(row);
        }

        _byColumns.Clear();
    }

    public TableDataChanges Build() =>
        new()
        {
            Table = Table,
            KeyColumns = KeyColumns,
            Columns = Columns,
            Changes = _changes,
            TargetRowCount = TargetRowCount
        };
}
