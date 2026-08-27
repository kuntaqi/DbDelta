using System.Diagnostics;
using DbDelta.Api.Contracts;
using DbDelta.Core.Comparison;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Services;

public sealed class CompareService
{
    private readonly IDatabaseProvider _provider;
    private readonly ConnectionFactory _connections;
    private readonly ServerClassifier _classifier;
    private readonly CompareSessionStore _sessions;
    private readonly SafetyOptions _safety;

    public CompareService(
        IDatabaseProvider provider,
        ConnectionFactory connections,
        ServerClassifier classifier,
        CompareSessionStore sessions,
        IOptions<SafetyOptions> safety)
    {
        _provider = provider;
        _connections = connections;
        _classifier = classifier;
        _sessions = sessions;
        _safety = safety.Value;
    }

    public async Task<ProbeResponse> ProbeAsync(ConnectionRequest request, CancellationToken cancellationToken)
    {
        var resolved = _connections.Resolve(request);
        var info = await _provider.ProbeAsync(resolved.ConnectionString, cancellationToken).ConfigureAwait(false);
        var schema = await _provider.CreateSchemaReader(resolved.ConnectionString).ReadAsync(cancellationToken).ConfigureAwait(false);

        return new ProbeResponse(
            resolved.Server,
            info.DatabaseName,
            info.ProductVersion,
            info.Edition,
            info.Collation,
            _classifier.Classify(resolved.Server),
            _classifier.IsReadOnly(resolved.Server),
            schema.Tables.Count,
            schema.Views.Count,
            schema.Routines.Count,
            schema.ReadWarnings);
    }

    public async Task<CompareResponse> CompareAsync(CompareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source_ = _connections.Resolve(request.Source);
        var target_ = _connections.Resolve(request.Target);
        var sourceConnection = source_.ConnectionString;
        var targetConnection = target_.ConnectionString;

        var stopwatch = Stopwatch.StartNew();
        var source = await _provider.CreateSchemaReader(sourceConnection).ReadAsync(cancellationToken).ConfigureAwait(false);
        var target = await _provider.CreateSchemaReader(targetConnection).ReadAsync(cancellationToken).ConfigureAwait(false);
        var diff = new SchemaComparer().Compare(source, target);
        stopwatch.Stop();

        var session = new CompareSession
        {
            Id = Guid.NewGuid().ToString("n"),
            Source = source,
            Target = target,
            Diff = diff,
            SourceServer = source_.Server,
            TargetServer = target_.Server,
            SourceConnectionString = sourceConnection,
            TargetConnectionString = targetConnection,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ComparedAt = DateTimeOffset.UtcNow
        };

        _sessions.Add(session);
        return Describe(session);
    }

    public CompareResponse Describe(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new CompareResponse(
            session.Id,
            session.Source.DatabaseName,
            session.Target.DatabaseName,
            _classifier.Classify(session.SourceServer),
            _classifier.Classify(session.TargetServer),
            _classifier.IsReadOnly(session.TargetServer),
            session.DurationMs,
            session.Target.IsEmpty,
            Warnings(session),
            Counts(session),
            session.Diff.Objects.Select(o => Summarise(session, o)).ToList());
    }

    public ObjectDetail? Detail(CompareSession session, string objectId)
    {
        ArgumentNullException.ThrowIfNull(session);

        var diff = session.Diff.Objects.FirstOrDefault(o => session.IdOf(o.Identity) == objectId);
        if (diff is null)
        {
            return null;
        }

        var script = new TSqlEmitterAdapter(_provider).Emit(session, new HashSet<ObjectIdentity> { diff.Identity });

        return new ObjectDetail(
            Summarise(session, diff),
            diff.Properties.Select(p => new PropertyDto(p.Property, p.Source, p.Target)).ToList(),
            diff.Children.Select(c => Summarise(session, c)).ToList(),
            diff.Children
                .SelectMany(c => c.Properties.Select(p => new PropertyDto($"{c.Identity.Name}.{p.Property}", p.Source, p.Target)))
                .ToList(),
            DefinitionOf(session.Source, diff.Identity),
            DefinitionOf(session.Target, diff.Identity),
            script.Steps.Select(s => s.Sql).ToList());
    }

    public async Task<ScriptResponse> ScriptAsync(
        CompareSession session,
        DataCompareService data,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(data);

        // What was ticked is not always what has to run. A table picked without the table its foreign key
        // points at emits an FK to something that does not exist, so the plan is expanded before emission
        // and what the expansion added is reported alongside the script.
        var closure = SchemaSelectionService.Closure(session);
        var script = new TSqlEmitterAdapter(_provider).Emit(session, closure.Selection);
        var steps = script.Steps.ToList();
        var rows = await DataStepsAsync(session, data, cancellationToken).ConfigureAwait(false);
        steps.AddRange(rows.Steps);

        // Building a script is the only moment anyone can have reviewed one, so this is where the row
        // state behind it is recorded. Apply compares what it finds now against this.
        session.ReviewedRows = rows.Rows;

        var combined = new SyncScript
        {
            Header = script.Header,
            Steps = steps.OrderBy(s => s.Phase).ToList()
        };

        var sql = combined.ToSql();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(sql);

        return new ScriptResponse(
            sql,
            combined.Count,
            bytes,
            bytes > _safety.MaxReviewableScriptBytes,
            combined.Steps.Select(s => new StepDto(s.Phase.ToString(), s.Description, s.Sql)).ToList(),
            rows.DeleteWarnings,
            closure.Required.Select(r => SchemaSelectionService.Required(session, r)).ToList(),
            [.. closure.Unsatisfiable, .. closure.Blocked.Select(SchemaSelectionService.Conflict), .. rows.ClosureWarnings],
            rows.RequiredRows
                .Select(r => new RequiredRowsDto(
                    r.Table.QualifiedName, r.RowCount, r.RequiredBy.QualifiedName, r.ForeignKeyName))
                .ToList(),
            SchemaSelectionService.Exclusions(session));
    }

    // Deletes walk the foreign key graph child-first and inserts parent-first. Both directions in one
    // pass would break one of them, so the two phases are filled in opposite orders.
    private async Task<DataScriptResult> DataStepsAsync(
        CompareSession session,
        DataCompareService data,
        CancellationToken cancellationToken)
    {
        if (session.DataSelections.Count == 0)
        {
            return DataScriptResult.Empty;
        }

        var order = TableDependencyGraph.Build(session.Source.Tables).OrderForData();
        var ranked = order.Ordered.Concat(order.Cyclic).ToList();

        var picked = new List<TableDataChanges>();

        foreach (var table in ranked.Where(session.DataSelections.ContainsKey))
        {
            var changes = await data
                .ChangesAsync(session, table, session.DataSelections[table], cancellationToken)
                .ConfigureAwait(false);

            if (changes is not null)
            {
                picked.Add(changes);
            }
        }

        // The rows about to be written may point at parents the target does not have, and the plan has to
        // carry those before it carries the rows that need them.
        var closure = await new ParentClosure(
                _provider.CreateRowByValueReader(session.SourceConnectionString),
                _provider.CreateRowByValueReader(session.TargetConnectionString),
                _safety.MaxClosureRows)
            .ExpandAsync(session.Source, session.Target, picked, session.KeyFor, cancellationToken)
            .ConfigureAwait(false);

        var byTable = closure.Tables.ToDictionary(t => t.Table.Identity);
        var warnings = new List<string>();
        var emitted = new List<IReadOnlyList<ScriptStep>>();

        // Ordered again over the whole set rather than over what was picked: closure can add a table
        // nobody selected, and it has to land ahead of the table that needed it.
        foreach (var table in ranked.Where(byTable.ContainsKey))
        {
            var changes = byTable[table];
            var deletes = changes.Changes.Count(c => c.Classification == RowClassification.Delete);

            // Two deletes out of five rows is a different decision from two out of five million, so the
            // share is what gets reported rather than the raw count.
            if (deletes > 0 && changes.TargetRowCount > 0)
            {
                var share = (double)deletes / changes.TargetRowCount;
                if (share > _safety.MaxDeleteShare)
                {
                    warnings.Add(
                        $"{table.QualifiedName}: {deletes} of {changes.TargetRowCount} target rows would be deleted "
                        + $"({share:P1}, over the {_safety.MaxDeleteShare:P0} limit).");
                }
            }

            emitted.Add(_provider.CreateDataScriptEmitter().Emit(changes));
        }

        var steps = new List<ScriptStep>();

        foreach (var produced in Enumerable.Reverse(emitted))
        {
            steps.AddRange(produced.Where(s => s.Phase == ScriptPhase.DataDeletes));
        }

        foreach (var produced in emitted)
        {
            steps.AddRange(produced.Where(s => s.Phase == ScriptPhase.DataUpserts));
        }

        return new DataScriptResult
        {
            Steps = steps,
            DeleteWarnings = warnings,
            RequiredRows = closure.Added,
            ClosureWarnings = closure.Warnings,
            Rows = RowStateSnapshot.From(closure.Tables)
        };
    }

    private static IReadOnlyList<string> Warnings(CompareSession session)
    {
        var warnings = new List<string>();

        if (!string.Equals(session.Source.Collation, session.Target.Collation, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"Source is {session.Source.Collation} and target is {session.Target.Collation}. "
                + "String comparison differs between them, so data compare results cannot be trusted until this is resolved.");
        }

        warnings.AddRange(session.Source.ReadWarnings.Select(w => $"Source: {w}"));
        warnings.AddRange(session.Target.ReadWarnings.Select(w => $"Target: {w}"));

        return warnings;
    }

    private static IReadOnlyList<TypeCount> Counts(CompareSession session) =>
        session.Diff.Objects
            .GroupBy(o => o.Identity.Type)
            .Select(g => new TypeCount(g.Key.ToString(), g.Count(o => o.HasChanges), g.Count()))
            .OrderBy(c => c.Type, StringComparer.Ordinal)
            .ToList();

    private static ObjectSummary Summarise(CompareSession session, ObjectDiff diff) =>
        new(session.IdOf(diff.Identity),
            diff.Identity.Type.ToString(),
            diff.Identity.Schema,
            diff.Identity.Name,
            diff.Identity.QualifiedName,
            diff.Kind.ToString(),
            Describe(diff),
            diff.DifferingChildren.Count());

    private static string Describe(ObjectDiff diff)
    {
        if (diff.Kind == DiffKind.SourceOnly)
        {
            return "only on source";
        }

        if (diff.Kind == DiffKind.TargetOnly)
        {
            return "only on target";
        }

        if (diff.Kind == DiffKind.Same)
        {
            return "identical";
        }

        var parts = new List<string>();
        var columns = diff.DifferingChildren.Count(c => c.Identity.Type == ObjectType.Column);
        var indexes = diff.DifferingChildren.Count(c => c.Identity.Type == ObjectType.Index);
        var others = diff.DifferingChildren.Count() - columns - indexes;

        if (columns > 0)
        {
            parts.Add($"{columns} column{(columns == 1 ? string.Empty : "s")}");
        }

        if (indexes > 0)
        {
            parts.Add($"{indexes} index{(indexes == 1 ? string.Empty : "es")}");
        }

        if (others > 0)
        {
            parts.Add($"{others} constraint{(others == 1 ? string.Empty : "s")}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) + " differ" : "body differs";
    }

    private static string? DefinitionOf(DatabaseSchema schema, ObjectIdentity identity) => identity.Type switch
    {
        ObjectType.View => schema.Views.FirstOrDefault(v => v.Identity == identity)?.Definition,
        ObjectType.Routine => schema.Routines.FirstOrDefault(r => r.Identity == identity)?.Definition,
        ObjectType.Trigger => schema.Triggers.FirstOrDefault(t => t.Identity == identity)?.Definition,
        ObjectType.Table => TableSketch(schema, identity),
        _ => null
    };

    // Tables have no stored definition, so a readable sketch stands in for the side-by-side view.
    private static string? TableSketch(DatabaseSchema schema, ObjectIdentity identity)
    {
        var table = schema.Tables.FirstOrDefault(t => t.Identity == identity);
        if (table is null)
        {
            return null;
        }

        var lines = new List<string> { $"CREATE TABLE {identity.QualifiedName} (" };
        lines.AddRange(table.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => $"    {c.Name} {c.DataType}{(c.IsNullable ? " NULL" : " NOT NULL")},"));

        if (table.PrimaryKey is not null)
        {
            lines.Add($"    CONSTRAINT {table.PrimaryKey.Name} PRIMARY KEY ({string.Join(", ", table.PrimaryKey.Columns.Select(c => c.Name))})");
        }

        lines.Add(");");
        lines.AddRange(table.Indexes.Select(i =>
            $"{(i.IsUnique ? "CREATE UNIQUE INDEX" : "CREATE INDEX")} {i.Name} ON {identity.Name} ({string.Join(", ", i.Columns.Select(c => c.Name))});"));
        lines.AddRange(table.CheckConstraints.Select(c => $"CHECK {c.Name} {c.Expression}"));
        lines.AddRange(table.ForeignKeys.Select(f =>
            $"FOREIGN KEY {f.Name} ({string.Join(", ", f.Columns)}) REFERENCES {f.ReferencedTable.QualifiedName}"));

        return string.Join('\n', lines);
    }
}
