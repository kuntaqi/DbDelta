using DbDelta.Api.Contracts;
using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

public sealed class ApplyService
{
    private readonly IDatabaseProvider _provider;
    private readonly IScriptExecutor _executor;
    private readonly ServerClassifier _classifier;
    private readonly CompareService _compare;
    private readonly DataCompareService _data;
    private readonly RunLogStore _runs;

    public ApplyService(
        IDatabaseProvider provider,
        IScriptExecutor executor,
        ServerClassifier classifier,
        CompareService compare,
        DataCompareService data,
        RunLogStore runs)
    {
        _provider = provider;
        _executor = executor;
        _classifier = classifier;
        _compare = compare;
        _data = data;
        _runs = runs;
    }

    public async Task<ApplyResponse> ApplyAsync(
        CompareSession session,
        ApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var script = await _compare.ScriptAsync(session, request.Include, _data, cancellationToken).ConfigureAwait(false);
        var destructive = Destructive(script);

        // An empty plan has nothing to commit, and logging it as a successful run would fill the log
        // with entries that did nothing.
        if (script.StepCount == 0)
        {
            return Respond(
                ApplyResult.Blocked("Nothing to apply — the target already matches for everything selected.", []),
                0,
                destructive);
        }

        var blockers = Guards(session, request, destructive);
        if (blockers.Count > 0)
        {
            return Respond(ApplyResult.Blocked("Apply refused.", blockers), script.StepCount, destructive);
        }

        // Between the comparison and this click the target may have moved. Applying a script built
        // against a database that no longer matches is how a "safe" tool corrupts one.
        var drift = await DriftAsync(session, cancellationToken).ConfigureAwait(false);
        if (drift is not null)
        {
            await LogAsync(session, "Apply", "Drifted", script, 0, 0, drift, cancellationToken).ConfigureAwait(false);

            return new ApplyResponse(
                nameof(ApplyOutcome.Drifted),
                "The target changed since the comparison. Compare again before applying.",
                script.StepCount,
                0,
                drift,
                null,
                [],
                destructive);
        }

        var result = await _executor
            .ExecuteAsync(session.TargetConnectionString, script.Sql, script.StepCount, cancellationToken)
            .ConfigureAwait(false);

        await LogAsync(
            session,
            "Apply",
            result.Outcome.ToString(),
            script,
            result.StepCount,
            result.DurationMs,
            result.ServerMessage,
            cancellationToken).ConfigureAwait(false);

        return Respond(result, script.StepCount, destructive);
    }

    public async Task<IReadOnlyList<RunLogEntryDto>> RunsAsync(CancellationToken cancellationToken)
    {
        var entries = await _runs.ListAsync(50, cancellationToken).ConfigureAwait(false);

        return entries
            .Select(e => new RunLogEntryDto(
                e.Id, e.At, e.Action, e.Route, e.Outcome, e.StepCount, e.DurationMs, e.ServerMessage))
            .ToList();
    }

    public Task<RunLogEntry?> RunAsync(string id, CancellationToken cancellationToken) =>
        _runs.FindAsync(id, cancellationToken);

    private List<string> Guards(CompareSession session, ApplyRequest request, IReadOnlyList<string> destructive) =>
        ApplyGuards.Evaluate(new ApplyGuardContext
        {
            TargetServer = session.TargetServer,
            TargetDatabase = session.Target.DatabaseName,
            Confirmation = request.Confirmation,
            TargetIsReadOnly = _classifier.IsReadOnly(session.TargetServer),
            AllowDestructive = request.AllowDestructive,
            DestructiveSteps = destructive
        }).ToList();

    private async Task<string?> DriftAsync(CompareSession session, CancellationToken cancellationToken)
    {
        var current = await _provider
            .CreateSchemaReader(session.TargetConnectionString)
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        var drift = new SchemaComparer().Compare(session.Target, current);
        var changed = drift.Differing.Take(5).Select(o => o.Identity.QualifiedName).ToList();

        return changed.Count == 0
            ? null
            : $"{drift.Differing.Count()} object(s) no longer match what was compared: {string.Join(", ", changed)}";
    }

    // Dropped objects and deleted rows are the parts a later rollback cannot bring back, so they need
    // the same deliberate consent. Over-limit delete shares join the list rather than sitting in a
    // separate warning nobody has to acknowledge.
    private static IReadOnlyList<string> Destructive(ScriptResponse script) =>
        script.Steps
            .Where(s => s.Sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase)
                || s.Sql.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase)
                || s.Sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Description)
            .Concat(script.DeleteWarnings)
            .ToList();

    private static ApplyResponse Respond(ApplyResult result, int stepCount, IReadOnlyList<string> destructive) =>
        new(result.Outcome.ToString(),
            result.Message,
            stepCount,
            result.DurationMs,
            result.ServerMessage,
            result.ErrorNumber,
            result.Blockers,
            destructive);

    private async Task LogAsync(
        CompareSession session,
        string action,
        string outcome,
        ScriptResponse script,
        int stepCount,
        long durationMs,
        string? serverMessage,
        CancellationToken cancellationToken) =>
        await _runs.WriteAsync(
            new RunLogEntry
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                At = DateTimeOffset.Now,
                Action = action,
                SourceDatabase = session.Source.DatabaseName,
                TargetServer = session.TargetServer,
                TargetDatabase = session.Target.DatabaseName,
                Outcome = outcome,
                StepCount = stepCount == 0 ? script.StepCount : stepCount,
                DurationMs = durationMs,
                ServerMessage = serverMessage,
                Sql = script.Sql
            },
            cancellationToken).ConfigureAwait(false);
}
