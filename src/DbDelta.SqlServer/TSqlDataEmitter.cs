using DbDelta.Core.Data;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer;

public sealed class TSqlDataEmitter : IDataScriptEmitter
{
    // INSERT ... VALUES accepts at most 1000 row constructors, so batches stop there rather than
    // producing a statement the server rejects.
    private const int RowsPerInsert = 1000;

    public IReadOnlyList<ScriptStep> Emit(TableDataChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var quoter = SqlServerQuoter.Instance;
        var table = quoter.Qualify(changes.Table.Identity);
        var name = changes.Table.Identity.QualifiedName;
        var steps = new List<ScriptStep>();

        var deletes = changes.Changes.Where(c => c.Classification == RowClassification.Delete).ToList();
        var inserts = changes.Changes.Where(c => c.Classification == RowClassification.Insert).ToList();
        var updates = changes.Changes.Where(c => c.Classification == RowClassification.Update).ToList();

        if (deletes.Count > 0)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DataDeletes,
                $"delete {deletes.Count} row(s) from {name}",
                string.Join("\n", deletes.Select(d => Delete(changes, table, d)))));
        }

        foreach (var batch in inserts.Chunk(RowsPerInsert))
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DataUpserts,
                $"insert {batch.Length} row(s) into {name}",
                Insert(changes, table, batch)));
        }

        if (updates.Count > 0)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DataUpserts,
                $"update {updates.Count} row(s) in {name}",
                string.Join("\n", updates.Select(u => Update(changes, table, u)))));
        }

        // A fresh IDENTITY starts at 1, so rows inserted with explicit keys leave the seed behind and
        // the application's next insert collides with one of them.
        if (steps.Count > 0 && inserts.Count > 0 && changes.HasIdentityInsert)
        {
            var identity = changes.Table.Columns.First(c => c.Identity is not null).Name;

            steps.Add(new ScriptStep(
                ScriptPhase.DataUpserts,
                $"reseed identity on {name}",
                $"DBCC CHECKIDENT ('{name}', RESEED) WITH NO_INFOMSGS; -- {identity}"));
        }

        return steps;
    }

    private static string Insert(TableDataChanges changes, string table, IReadOnlyList<DataChange> rows)
    {
        var quoter = SqlServerQuoter.Instance;
        var columns = changes.KeyColumns.Concat(changes.Columns).ToList();
        var columnList = string.Join(", ", columns.Select(quoter.Quote));

        var values = rows.Select(row =>
            "    (" + string.Join(", ", columns.Select(c => TSqlLiteral.For(changes.Table, c, Value(row, c)))) + ")");

        var statement = $"INSERT INTO {table} ({columnList})\nVALUES\n{string.Join(",\n", values)};";

        return changes.HasIdentityInsert
            ? $"SET IDENTITY_INSERT {table} ON;\n{statement}\nSET IDENTITY_INSERT {table} OFF;"
            : statement;
    }

    private static string Update(TableDataChanges changes, string table, DataChange row)
    {
        var quoter = SqlServerQuoter.Instance;

        var assignments = string.Join(", ", changes.Columns
            .Select(c => $"{quoter.Quote(c)} = {TSqlLiteral.For(changes.Table, c, Value(row, c))}"));

        return $"UPDATE {table} SET {assignments} WHERE {Where(changes, row)};";
    }

    private static string Delete(TableDataChanges changes, string table, DataChange row) =>
        $"DELETE FROM {table} WHERE {Where(changes, row)};";

    private static string Where(TableDataChanges changes, DataChange row)
    {
        var quoter = SqlServerQuoter.Instance;

        return string.Join(" AND ", changes.KeyColumns.Select(c =>
            $"{quoter.Quote(c)} = {TSqlLiteral.For(changes.Table, c, row.KeyValues.GetValueOrDefault(c))}"));
    }

    private static string? Value(DataChange row, string column) =>
        row.SourceValues.TryGetValue(column, out var value)
            ? value
            : row.KeyValues.GetValueOrDefault(column);
}
