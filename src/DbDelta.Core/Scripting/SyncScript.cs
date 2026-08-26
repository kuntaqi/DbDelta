using System.Text;

namespace DbDelta.Core.Scripting;

public sealed class SyncScript
{
    public required IReadOnlyList<ScriptStep> Steps { get; init; }

    public required string Header { get; init; }

    public bool IsEmpty => Steps.Count == 0;

    public int Count => Steps.Count;

    public IEnumerable<ScriptStep> InPhase(ScriptPhase phase) => Steps.Where(s => s.Phase == phase);

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
            sql.AppendLine(item.Sql);
            sql.AppendLine();
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }
}
