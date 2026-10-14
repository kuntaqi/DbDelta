using System.Diagnostics;
using DbDelta.Core.Apply;
using DbDelta.Core.Scripting;
using Npgsql;
using NpgsqlTypes;

namespace DbDelta.PostgreSql;

// Applies a PostgreSQL sync script under one connection and one transaction, so a failure anywhere rolls the
// whole plan back and leaves the target unchanged. DDL is transactional in PostgreSQL, which is what makes
// all-or-nothing apply possible at all; the same single connection is also what a temp staging table and its
// COPY need, since both are session-scoped.
public sealed class PostgreSqlScriptExecutor : IScriptExecutor
{
    public async Task<ApplyResult> ExecuteAsync(
        string connectionString,
        SyncScript script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(script);

        var stopwatch = Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var step in script.Steps)
            {
                if (step.Load is { } load)
                {
                    await CopyAsync(connection, load, cancellationToken).ConfigureAwait(false);
                    continue;
                }

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

    // A staged step's rows travel beside the script. COPY FROM STDIN streams them into the staging table on the
    // same connection and inside the same transaction, the PostgreSQL counterpart of SqlBulkCopy. Every staging
    // column is text, so each value is written as text (or NULL), exactly as it was read.
    private static async Task CopyAsync(NpgsqlConnection connection, BulkLoad load, CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", load.Columns.Select(PostgreSqlQuoter.Instance.Quote));
        var copy = $"COPY {PostgreSqlQuoter.Instance.Quote(load.StagingTable)} ({columns}) FROM STDIN (FORMAT BINARY)";

        await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken).ConfigureAwait(false);
        foreach (var row in load.Rows)
        {
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            foreach (var value in row)
            {
                if (value is null)
                {
                    await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await importer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
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
