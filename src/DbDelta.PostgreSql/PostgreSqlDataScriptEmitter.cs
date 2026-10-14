using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

// Turns a table's row changes into PostgreSQL DML, mirroring TSqlDataEmitter. Deletes are always inline (a
// delete writes only its key); inserts and updates go inline as literals until the estimated size crosses
// maxInlineBytes, past which they travel through a staging table (PgStagedWriter). After explicit-key inserts
// into an identity table the owning sequence is reseeded, so the application's next insert does not collide — the
// PostgreSQL equivalent of DBCC CHECKIDENT RESEED.
public sealed class PostgreSqlDataScriptEmitter : IDataScriptEmitter
{
    private const int RowsPerInsert = 1000;

    private static readonly PostgreSqlQuoter Q = PostgreSqlQuoter.Instance;

    public IReadOnlyList<ScriptStep> Emit(TableDataChanges changes, long maxInlineBytes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var qualified = Q.Qualify(changes.Table.Identity);
        var types = changes.Table.Columns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        var deletes = changes.Changes.Where(c => c.Classification == RowClassification.Delete).ToList();
        var inserts = changes.Changes.Where(c => c.Classification == RowClassification.Insert).ToList();
        var updates = changes.Changes.Where(c => c.Classification == RowClassification.Update).ToList();

        var steps = new List<ScriptStep>();

        if (deletes.Count > 0)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DataDeletes, $"delete {deletes.Count} row(s) from {changes.Table.Identity.QualifiedName}",
                string.Join("\n", deletes.Select(d => DeleteRow(qualified, changes, d, types))), Destructive: true));
        }

        var staged = ScriptSizeEstimator.EstimateBytes(changes) > maxInlineBytes && inserts.Count + updates.Count > 0;

        if (staged)
        {
            steps.AddRange(PgStagedWriter.Emit(changes, inserts, updates));
        }
        else
        {
            foreach (var chunk in Chunk(inserts, RowsPerInsert))
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DataUpserts, $"insert {chunk.Count} row(s) into {changes.Table.Identity.QualifiedName}",
                    InsertChunk(qualified, changes, chunk, types)));
            }

            if (updates.Count > 0)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DataUpserts, $"update {updates.Count} row(s) in {changes.Table.Identity.QualifiedName}",
                    string.Join("\n", updates.Select(u => UpdateRow(qualified, changes, u, types)))));
            }
        }

        if (inserts.Count > 0 && changes.HasIdentityInsert)
        {
            foreach (var column in IdentityColumns(changes))
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DataUpserts, $"reseed identity {changes.Table.Identity.QualifiedName}.{column}",
                    Reseed(qualified, column)));
            }
        }

        return steps;
    }

    private static string InsertChunk(
        string qualified, TableDataChanges changes, IReadOnlyList<DataChange> chunk,
        IReadOnlyDictionary<string, DataTypeSpec> types)
    {
        var columns = AllColumns(changes);
        var header = string.Join(", ", columns.Select(Q.Quote));
        var rows = chunk.Select(c =>
            "(" + string.Join(", ", columns.Select(col => PgLiteral.For(types[col], Value(c, col)))) + ")");
        return $"INSERT INTO {qualified} ({header})\nVALUES\n{string.Join(",\n", rows)};";
    }

    private static string UpdateRow(
        string qualified, TableDataChanges changes, DataChange change, IReadOnlyDictionary<string, DataTypeSpec> types)
    {
        var assignments = string.Join(", ", changes.Columns.Select(c =>
            $"{Q.Quote(c)} = {PgLiteral.For(types[c], Value(change, c))}"));
        return $"UPDATE {qualified} SET {assignments} WHERE {KeyPredicate(changes, change, types)};";
    }

    private static string DeleteRow(
        string qualified, TableDataChanges changes, DataChange change, IReadOnlyDictionary<string, DataTypeSpec> types) =>
        $"DELETE FROM {qualified} WHERE {KeyPredicate(changes, change, types)};";

    private static string KeyPredicate(
        TableDataChanges changes, DataChange change, IReadOnlyDictionary<string, DataTypeSpec> types) =>
        string.Join(" AND ", changes.KeyColumns.Select(c =>
            $"{Q.Quote(c)} = {PgLiteral.For(types[c], change.KeyValues.GetValueOrDefault(c))}"));

    // Set the sequence to the current maximum; if the table is empty, to 1 with is_called false so the next
    // value is 1. pg_get_serial_sequence resolves the sequence owned by an identity or serial column.
    private static string Reseed(string qualified, string column)
    {
        var sequence = $"pg_get_serial_sequence('{qualified.Replace("'", "''", StringComparison.Ordinal)}', "
            + $"'{column.Replace("'", "''", StringComparison.Ordinal)}')";
        var col = Q.Quote(column);
        return $"SELECT setval({sequence}, coalesce((SELECT max({col}) FROM {qualified}), 1), "
            + $"(SELECT count(*) FROM {qualified}) > 0);";
    }

    private static IEnumerable<string> IdentityColumns(TableDataChanges changes)
    {
        var used = changes.Columns.Concat(changes.KeyColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return changes.Table.Columns
            .Where(c => c.Identity is not null && used.Contains(c.Name))
            .Select(c => c.Name);
    }

    private static IReadOnlyList<string> AllColumns(TableDataChanges changes) =>
        changes.KeyColumns.Concat(changes.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Value(DataChange change, string column) =>
        change.SourceValues.TryGetValue(column, out var value) ? value : change.KeyValues.GetValueOrDefault(column);

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }
}
