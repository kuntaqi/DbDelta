namespace DbDelta.Core.Apply;

public interface IScriptExecutor
{
    Task<ApplyResult> ExecuteAsync(
        string connectionString,
        string sql,
        int stepCount,
        CancellationToken cancellationToken = default);
}
