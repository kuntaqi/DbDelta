using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

// The bulk data path, mirroring TSqlStagedWriter: rows land in an all-text temp table and one set-based
// statement each moves them to the target, so the script stays a constant size no matter the row count. Every
// staging column is text — the value is already text — so the conversion to the real type happens once,
// server-side, in the statement that reads the column, the same place a literal would be converted.
internal static class PgStagedWriter
{
    private static readonly PostgreSqlQuoter Q = PostgreSqlQuoter.Instance;

    public static IReadOnlyList<ScriptStep> Emit(
        TableDataChanges changes, IReadOnlyList<DataChange> inserts, IReadOnlyList<DataChange> updates)
    {
        var staging = StagingName(changes.Table.Identity);
        var stagingSql = Q.Quote(staging);
        var qualified = Q.Qualify(changes.Table.Identity);
        var columns = AllColumns(changes);
        var types = changes.Table.Columns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        var steps = new List<ScriptStep>
        {
            new(ScriptPhase.DataUpserts, $"stage {changes.Table.Identity.QualifiedName}", Create(stagingSql, columns)),
            new(ScriptPhase.DataUpserts, $"load {inserts.Count + updates.Count} row(s)",
                $"COPY {stagingSql} FROM '{DataFileName(changes.Table.Identity)}' WITH (FORMAT csv, ENCODING 'UTF8');",
                Load(changes, inserts, updates, staging, columns)),
            new(ScriptPhase.DataUpserts, $"insert staged rows into {changes.Table.Identity.QualifiedName}",
                InsertSelect(qualified, stagingSql, columns, types)),
        };

        if (updates.Count > 0)
        {
            steps.Add(new ScriptStep(ScriptPhase.DataUpserts,
                $"update {changes.Table.Identity.QualifiedName} from staged rows",
                UpdateJoin(qualified, stagingSql, changes, types)));
        }

        steps.Add(new ScriptStep(ScriptPhase.DataUpserts, $"drop staging for {changes.Table.Identity.QualifiedName}",
            $"DROP TABLE {stagingSql};"));

        return steps;
    }

    private static string Create(string staging, IReadOnlyList<string> columns)
    {
        var defs = new List<string> { "    \"_op\" text NOT NULL" };
        defs.AddRange(columns.Select(c => $"    {Q.Quote(c)} text NULL"));
        return $"CREATE TEMP TABLE {staging} (\n{string.Join(",\n", defs)}\n);";
    }

    private static BulkLoad Load(
        TableDataChanges changes, IReadOnlyList<DataChange> inserts, IReadOnlyList<DataChange> updates,
        string staging, IReadOnlyList<string> columns)
    {
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var insert in inserts)
        {
            rows.Add(Row("I", insert, columns));
        }

        foreach (var update in updates)
        {
            rows.Add(Row("U", update, columns));
        }

        IReadOnlyList<string> loadColumns = ["_op", .. columns];
        return new BulkLoad(
            Q.Qualify(changes.Table.Identity), staging, DataFileName(changes.Table.Identity), loadColumns, rows);
    }

    private static IReadOnlyList<string?> Row(string op, DataChange change, IReadOnlyList<string> columns)
    {
        var row = new List<string?> { op };
        row.AddRange(columns.Select(c => Value(change, c)));
        return row;
    }

    private static string InsertSelect(
        string qualified, string staging, IReadOnlyList<string> columns, IReadOnlyDictionary<string, DataTypeSpec> types)
    {
        var target = string.Join(", ", columns.Select(Q.Quote));
        var select = string.Join(", ", columns.Select(c => Cast(c, types)));
        return $"INSERT INTO {qualified} ({target})\nSELECT {select}\nFROM {staging}\nWHERE \"_op\" = 'I';";
    }

    private static string UpdateJoin(
        string qualified, string staging, TableDataChanges changes, IReadOnlyDictionary<string, DataTypeSpec> types)
    {
        var assignments = string.Join(",\n    ", changes.Columns.Select(c => $"{Q.Quote(c)} = s.{Cast(c, types)}"));
        var join = string.Join(" AND ", changes.KeyColumns.Select(c => $"t.{Q.Quote(c)} = s.{Cast(c, types)}"));
        return $"UPDATE {qualified} t SET\n    {assignments}\nFROM {staging} s\nWHERE {join} AND s.\"_op\" = 'U';";
    }

    // The staging column is text; cast it back to the real type where it is read, the reverse of the ::text the
    // reader rendered it with. decode(...) is bytea's reverse of encode(...,'hex').
    private static string Cast(string column, IReadOnlyDictionary<string, DataTypeSpec> types)
    {
        var quoted = Q.Quote(column);
        return types.TryGetValue(column, out var type)
            ? $"{quoted}::{PgTypeText.Declare(type)}"
            : quoted;
    }

    private static IReadOnlyList<string> AllColumns(TableDataChanges changes) =>
        changes.KeyColumns.Concat(changes.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Value(DataChange change, string column) =>
        change.SourceValues.TryGetValue(column, out var value) ? value : change.KeyValues.GetValueOrDefault(column);

    private static string StagingName(ObjectIdentity table) =>
        $"dbdelta_stg_{table.Schema}_{table.Name}".Replace('.', '_');

    private static string DataFileName(ObjectIdentity table) => $"{table.Schema}.{table.Name}.dat";
}
