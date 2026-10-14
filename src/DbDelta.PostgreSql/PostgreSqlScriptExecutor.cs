using System.Diagnostics;
using DbDelta.Core.Apply;
using DbDelta.Core.Scripting;
using Npgsql;

namespace DbDelta.PostgreSql;

// Applies a PostgreSQL sync script under one connection and one transaction, so a failure anywhere rolls the
// whole plan back and leaves the target unchanged. DDL is transactional in PostgreSQL, which is what makes
// all-or-nothing schema apply possible at all.
//
// Scope note: this increment emits schema DDL only, which carries no BulkLoad steps. A step with rows (the
// data path) is refused here rather than half-applied — that executor half arrives with the data increment,
// where it will use COPY the way the SQL Server side uses SqlBulkCopy.
public sealed class PostgreSqlScriptExecutor : IScriptExecutor
{
    public async Task<ApplyResult> ExecuteAsync(
        string connectionString,
        SyncScript script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(script);

        if (script.HasLoads)
        {
            return ApplyResult.Blocked(
                "The PostgreSQL executor applies schema scripts only; data-row steps are not supported yet.",
                ["This script carries bulk-loaded rows, which the PostgreSQL data path does not emit yet."]);
        }

        var stopwatch = Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var step in script.Steps)
            {
                await using var command = new NpgsqlCommand(step.Sql, connection, transaction) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return new ApplyResult
            {
                Outcome = ApplyOutcome.Committed,
                Message = $"{script.Count} step(s) committed.",
                StepCount = script.Count,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (PostgresException ex)
        {
            stopwatch.Stop();
            await RollbackAsync(transaction, cancellationToken).ConfigureAwait(false);

            return new ApplyResult
            {
                Outcome = ApplyOutcome.RolledBack,
                Message = "The script failed and the whole transaction was rolled back. The target is unchanged.",
                StepCount = script.Count,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ServerMessage = ex.MessageText,
                ErrorNumber = int.TryParse(ex.SqlState, out var code) ? code : null,
            };
        }
    }

    // The transaction may already be aborted, in which case rolling back throws and there is nothing left to
    // undo; the original failure is what the caller needs to hear about.
    private static async Task RollbackAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (PostgresException)
        {
        }
    }
}
