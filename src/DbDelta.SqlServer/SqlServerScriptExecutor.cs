using System.Diagnostics;
using DbDelta.Core.Apply;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerScriptExecutor : IScriptExecutor
{
    public async Task<ApplyResult> ExecuteAsync(
        string connectionString,
        SyncScript script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(script);

        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // One connection for the whole apply, which is not incidental: a staging table is temporary, so
        // it exists only for the connection that made it, and a bulk load can only join a transaction
        // that lives on the same one.
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await RunAsync(connection, transaction, "SET XACT_ABORT ON; SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON;", cancellationToken)
                .ConfigureAwait(false);

            foreach (var step in script.Steps)
            {
                if (step.Load is { } load)
                {
                    await LoadAsync(connection, transaction, load, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RunAsync(connection, transaction, step.Sql, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return new ApplyResult
            {
                Outcome = ApplyOutcome.Committed,
                Message = $"{script.Count} step(s) committed.",
                StepCount = script.Count,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (SqlException ex)
        {
            stopwatch.Stop();
            await RollbackAsync(transaction, cancellationToken).ConfigureAwait(false);

            return new ApplyResult
            {
                Outcome = ApplyOutcome.RolledBack,
                Message = "The script failed and the whole transaction was rolled back. The target is unchanged.",
                StepCount = script.Count,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ServerMessage = ex.Message,
                ErrorNumber = ex.Number
            };
        }
    }

    private static async Task RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LoadAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        BulkLoad load,
        CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = load.StagingTable,
            BulkCopyTimeout = 0,
            BatchSize = 10000
        };

        // Mapped by name rather than by position: the staging table is created by the step before this
        // one, and relying on the two lists happening to agree would be a silent corruption waiting for
        // the first column reorder.
        foreach (var column in load.Columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        await bulk.WriteToServerAsync(new BulkRowReader(load), cancellationToken).ConfigureAwait(false);
    }

    // XACT_ABORT may already have killed the transaction, in which case rolling it back throws and there
    // is nothing left to undo anyway. The original failure is what the caller needs to hear about.
    private static async Task RollbackAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (SqlException)
        {
        }
    }
}
