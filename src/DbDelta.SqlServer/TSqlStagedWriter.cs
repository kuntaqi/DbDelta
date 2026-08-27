using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer;

// The staged path: rows land in a temporary table and one set-based statement each moves them into the
// target. What this buys is that the script stays the same size whether the table has 800 rows or 800,000
// — the reviewable artifact is four statements, and the data travels beside it.
//
// The staging table is entirely NVARCHAR(MAX). Every value in this tool is already text by the time it
// gets here, and a typed staging table would mean converting on the client, which is a second place for a
// datetime or a float to be got wrong. This way the conversion happens once, server-side, in the same
// statement that reads the column — the same conversion a literal would have gone through.
internal static class TSqlStagedWriter
{
    private static readonly SqlServerQuoter Q = SqlServerQuoter.Instance;

    private static readonly HashSet<string> BinaryTypes =
        new(StringComparer.OrdinalIgnoreCase) { "binary", "varbinary", "image", "timestamp", "rowversion" };

    private static readonly HashSet<string> DateTimeTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "time"
        };

    public static IReadOnlyList<ScriptStep> Emit(
        TableDataChanges changes,
        IReadOnlyList<DataChange> inserts,
        IReadOnlyList<DataChange> updates)
    {
        var name = changes.Table.Identity.QualifiedName;
        var table = Q.Qualify(changes.Table.Identity);
        var staging = StagingName(changes.Table.Identity);
        var columns = changes.KeyColumns.Concat(changes.Columns).ToList();
        var steps = new List<ScriptStep>();

        steps.Add(new ScriptStep(
            ScriptPhase.DataUpserts,
            $"stage {inserts.Count + updates.Count} row(s) for {name}",
            Create(staging, columns)));

        steps.Add(new ScriptStep(
            ScriptPhase.DataUpserts,
            $"load {inserts.Count + updates.Count} row(s) into {staging}",
            BulkInsert(staging, changes.Table.Identity),
            new BulkLoad(
                name,
                staging,
                DataFileName(changes.Table.Identity),
                ["_op", .. columns],
                Rows(inserts, updates, columns))));

        if (inserts.Count > 0)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DataUpserts,
                $"insert {inserts.Count} row(s) into {name} from {staging}",
                Wrap(changes, table, InsertSelect(changes, table, staging, columns))));
        }

        if (updates.Count > 0)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DataUpserts,
                $"update {updates.Count} row(s) in {name} from {staging}",
                UpdateJoin(changes, table, staging)));
        }

        steps.Add(new ScriptStep(
            ScriptPhase.DataUpserts,
            $"drop {staging}",
            $"DROP TABLE {staging};"));

        return steps;
    }

    // Temporary rather than a real table, so a failed apply leaves nothing behind to clean up and two
    // runs cannot collide over the same name. It lives on the connection, which is why the whole apply
    // runs on one.
    public static string StagingName(ObjectIdentity table) =>
        $"#dbdelta_{table.Schema}_{table.Name}".Replace('.', '_');

    public static string DataFileName(ObjectIdentity table) =>
        $"{table.Schema}.{table.Name}.dat";

    private static string Create(string staging, IReadOnlyList<string> columns)
    {
        var definitions = new[] { "[_op] NVARCHAR(1) NOT NULL" }
            .Concat(columns.Select(c => $"{Q.Quote(c)} NVARCHAR(MAX) NULL"));

        return $"CREATE TABLE {staging} (\n    {string.Join(",\n    ", definitions)}\n);";
    }

    // For someone running the script by hand. The apply path streams the same rows over the connection
    // instead, so this statement is what the file is for rather than what the tool uses.
    private static string BulkInsert(string staging, ObjectIdentity table) =>
        $"""
        BULK INSERT {staging}
        FROM '{DataFileName(table)}'
        WITH (FORMAT = 'CSV', FIELDTERMINATOR = ',', ROWTERMINATOR = '0x0a', CODEPAGE = '65001');
        """;

    private static string InsertSelect(
        TableDataChanges changes,
        string table,
        string staging,
        IReadOnlyList<string> columns)
    {
        var target = string.Join(", ", columns.Select(Q.Quote));
        var selected = string.Join(",\n       ", columns.Select(c => Convert(changes.Table, c)));

        return $"""
            INSERT INTO {table} ({target})
            SELECT {selected}
            FROM {staging}
            WHERE [_op] = N'I';
            """;
    }

    private static string UpdateJoin(TableDataChanges changes, string table, string staging)
    {
        var assignments = string.Join(",\n    ", changes.Columns
            .Select(c => $"t.{Q.Quote(c)} = {Convert(changes.Table, c, "s")}"));

        var join = string.Join(" AND ", changes.KeyColumns
            .Select(c => $"t.{Q.Quote(c)} = {Convert(changes.Table, c, "s")}"));

        return $"""
            UPDATE t SET
                {assignments}
            FROM {table} t
            JOIN {staging} s ON {join}
            WHERE s.[_op] = N'U';
            """;
    }

    // Same per-type reasoning as the literal writer, in reverse. Getting this wrong does not fail loudly,
    // it writes the wrong data, so an unknown type converts straight rather than being guessed at.
    private static string Convert(TableDefinition table, string column, string? alias = null)
    {
        var quoted = alias is null ? Q.Quote(column) : $"{alias}.{Q.Quote(column)}";

        var type = table.Columns
            .FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
            ?.DataType;

        if (type is null)
        {
            return quoted;
        }

        var declaration = SqlTypeText.Declare(type);

        // Text out of a binary column was written as hex, so it has to come back the same way rather
        // than being read as the characters "0x…".
        if (BinaryTypes.Contains(type.Name))
        {
            return $"CONVERT({declaration}, {quoted}, 2)";
        }

        // Style 126 is ISO 8601, which is exactly the shape the reader wrote.
        if (DateTimeTypes.Contains(type.Name))
        {
            return $"CONVERT({declaration}, {quoted}, 126)";
        }

        return $"CONVERT({declaration}, {quoted})";
    }

    // A fresh IDENTITY starts at 1, so explicit keys need the same bracketing the literal path uses.
    private static string Wrap(TableDataChanges changes, string table, string sql) =>
        changes.HasIdentityInsert
            ? $"SET IDENTITY_INSERT {table} ON;\n{sql}\nSET IDENTITY_INSERT {table} OFF;"
            : sql;

    private static IReadOnlyList<IReadOnlyList<string?>> Rows(
        IReadOnlyList<DataChange> inserts,
        IReadOnlyList<DataChange> updates,
        IReadOnlyList<string> columns)
    {
        var rows = new List<IReadOnlyList<string?>>(inserts.Count + updates.Count);

        foreach (var (change, op) in inserts.Select(i => (i, "I")).Concat(updates.Select(u => (u, "U"))))
        {
            var values = new List<string?>(columns.Count + 1) { op };

            values.AddRange(columns.Select(c =>
                change.SourceValues.TryGetValue(c, out var value)
                    ? value
                    : change.KeyValues.GetValueOrDefault(c)));

            rows.Add(values);
        }

        return rows;
    }
}
