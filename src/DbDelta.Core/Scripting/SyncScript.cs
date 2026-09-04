using System.Text;

namespace DbDelta.Core.Scripting;

public sealed class SyncScript
{
    public required IReadOnlyList<ScriptStep> Steps { get; init; }

    public required string Header { get; init; }

    // What the emitter would not write, and why. A plan that quietly does less than it shows is the
    // failure this exists to prevent: before it, an index kind with no DDL this tool can produce was
    // either emitted wrongly or skipped in silence, and both left the caller believing the target now
    // matched. These are not steps — nothing runs — so they are carried beside the script rather than in
    // it, and the caller reports them alongside the ones closure could not satisfy.
    public IReadOnlyList<string> Refusals { get; init; } = [];

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

        foreach (var refusal in Refusals)
        {
            sql.AppendLine($"-- not emitted: {refusal}");
        }

        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("SET NOCOUNT ON;");

        // Spatial and XML index DDL is refused outright unless QUOTED_IDENTIFIER is ON — msg 1934, which
        // says the SET options are wrong and not what is wrong with them. SqlClient connects with it ON, so
        // the apply path never needed it, but this script is meant to be runnable by hand and sqlcmd
        // defaults it OFF. Verified to take effect in the same batch as the CREATE, which matters because
        // this script has no GO in it.
        sql.AppendLine("SET QUOTED_IDENTIFIER ON;");
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
