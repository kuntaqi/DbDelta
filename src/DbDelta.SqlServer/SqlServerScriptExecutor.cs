using System.Diagnostics;
using DbDelta.Core.Apply;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerScriptExecutor : IScriptExecutor
{
    public async Task<ApplyResult> ExecuteAsync(
        string connectionString,
        string sql,
        int stepCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return new ApplyResult
            {
                Outcome = ApplyOutcome.Committed,
                Message = $"{stepCount} step(s) committed.",
                StepCount = stepCount,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (SqlException ex)
        {
            stopwatch.Stop();

            // The script sets XACT_ABORT ON and wraps everything in one transaction, so the server has
            // already rolled the whole thing back by the time this is reached. Nothing partial survives.
            return new ApplyResult
            {
                Outcome = ApplyOutcome.RolledBack,
                Message = "The script failed and the whole transaction was rolled back. The target is unchanged.",
                StepCount = stepCount,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ServerMessage = ex.Message,
                ErrorNumber = ex.Number
            };
        }
    }
}
