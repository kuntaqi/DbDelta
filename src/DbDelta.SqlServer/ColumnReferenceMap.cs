using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

// The rows of CatalogQueries.ColumnReferences, sorted by what holds the column: read once, before the
// columns and check constraints they are attached to are built.
internal sealed class ColumnReferenceMap
{
    private static readonly IReadOnlyList<string> None = [];

    private readonly Dictionary<string, List<string>> _computed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ObjectIdentity, Dictionary<ObjectIdentity, List<string>>> _modules = [];

    public void AddComputed(ObjectIdentity table, string computedColumn, string column) =>
        Add(_computed, Key(table, computedColumn), column);

    public void AddCheck(ObjectIdentity table, string check, string column) =>
        Add(_checks, Key(table, check), column);

    public void AddModule(ObjectIdentity table, ObjectIdentity module, string? column)
    {
        if (!_modules.TryGetValue(table, out var modules))
        {
            modules = [];
            _modules[table] = modules;
        }

        if (!modules.TryGetValue(module, out var columns))
        {
            columns = [];
            modules[module] = columns;
        }

        if (column is not null && !columns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            columns.Add(column);
        }
    }

    public IReadOnlyList<string> ComputedFrom(ObjectIdentity table, string column) =>
        _computed.TryGetValue(Key(table, column), out var list) ? list : None;

    public IReadOnlyList<string> CheckColumns(ObjectIdentity table, string check) =>
        _checks.TryGetValue(Key(table, check), out var list) ? list : None;

    public IEnumerable<(ObjectIdentity Table, SchemaBoundReference Reference)> SchemaBound() =>
        _modules.SelectMany(t => t.Value.Select(m => (t.Key, new SchemaBoundReference(m.Key, m.Value))));

    // NUL is the one character an identifier cannot hold, so it cannot make two keys collide.
    private static string Key(ObjectIdentity table, string name) => $"{table.Schema}\0{table.Name}\0{name}";

    private static void Add(Dictionary<string, List<string>> map, string key, string column)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        if (!list.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(column);
        }
    }
}
