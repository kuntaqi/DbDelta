using System.Text;

namespace DbDelta.Core.Scripting;

public sealed class SyncScript
{
    public required IReadOnlyList<ScriptStep> Steps { get; init; }

    public required string Header { get; init; }

    public bool IsEmpty => Steps.Count == 0;

    public int Count => Steps.Count;

    public IEnumerable<ScriptStep> InPhase(ScriptPhase phase) => Steps.Where(s => s.Phase == phase);

    // Steps whose rows travel beside the script rather than inside it.
    public IEnumerable<BulkLoad> Loads => Steps.Select(s => s.Load).OfType<BulkLoad>();

    public bool HasLoads => Steps.Any(s => s.Load is not null);

    // One transaction with XACT_ABORT on, so a failure anywhere rolls the whole thing back rather
    // than leaving the target half-migrated.
    public string ToSql()
    {
        var sql = new StringBuilder();
        sql.AppendLine(Header);
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("BEGIN TRANSACTION;");
        sql.AppendLine();

        var step = 0;
        foreach (var item in Steps)
        {
            step++;
            sql.AppendLine($"-- {step}/{Steps.Count}  {item.Description}");

            // A load step's rows are not in the script. Running it by hand means putting the data file
            // somewhere the *server* can read — this path is resolved by SQL Server, not by whoever runs
            // the script — which is the one thing about this script that is not self-contained.
            if (item.Load is { } load)
            {
                sql.AppendLine($"-- {load.RowCount} row(s) live in {load.DataFileName}, downloaded alongside this script.");
                sql.AppendLine($"-- The path below is resolved by the server, so put the file where the server can read it.");
            }

            sql.AppendLine(item.Sql);
            sql.AppendLine();
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }
}
