using DbDelta.Core.Scripting;

namespace DbDelta.Core.Apply;

public interface IScriptExecutor
{
    // Takes the plan rather than its text, because a staged step's rows are not in the text. The
    // transaction has to be the caller's for the same reason: a bulk load and the statements around it
    // only roll back together if they share one.
    Task<ApplyResult> ExecuteAsync(
        string connectionString,
        SyncScript script,
        CancellationToken cancellationToken = default);
}
