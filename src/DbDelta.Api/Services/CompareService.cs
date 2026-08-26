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

    public SchemaSelectionResponse SelectSchema(CompareSession session, SchemaSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var identity = session.Resolve(request.ObjectId)
            ?? throw new InvalidOperationException($"'{request.ObjectId}' is not part of this comparison.");

        var diff = session.Diff.Find(identity);
        if (diff is null || !diff.HasChanges)
        {
            throw new InvalidOperationException($"{identity.QualifiedName} has nothing to sync.");
        }

        if (request.Selected)
        {
            session.SchemaSelections.Add(identity);
        }
        else
        {
            session.SchemaSelections.Remove(identity);
        }

        return SchemaSelection(session);
    }

    public SchemaSelectionResponse SelectAllSchema(CompareSession session, bool selected)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.SchemaSelections.Clear();

        if (selected)
        {
            foreach (var diff in session.Diff.Differing)
            {
                session.SchemaSelections.Add(diff.Identity);
            }
        }

        return SchemaSelection(session);
    }

    public static SchemaSelectionResponse SchemaSelection(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SchemaSelectionResponse(
            session.SchemaSelections.Select(session.IdOf).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            session.Diff.Differing.Count(),
            session.DataSelections.Count);
    }

    public async Task<ScriptResponse> ScriptAsync(
        CompareSession session,
        DataCompareService data,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(data);

        var identities = session.SchemaSelections.ToHashSet();
        var script = new TSqlEmitterAdapter(_provider).Emit(session, identities);
        var steps = script.Steps.ToList();
        var (dataSteps, deleteWarnings) = await DataStepsAsync(session, data, cancellationToken).ConfigureAwait(false);
        steps.AddRange(dataSteps);

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
            deleteWarnings);
    }

    // Deletes walk the foreign key graph child-first and inserts parent-first. Both directions in one
    // pass would break one of them, so the two phases are filled in opposite orders.
    private async Task<(List<ScriptStep> Steps, List<string> DeleteWarnings)> DataStepsAsync(
        CompareSession session,
        DataCompareService data,
        CancellationToken cancellationToken)
    {
        var steps = new List<ScriptStep>();
        var warnings = new List<string>();

        if (session.DataSelections.Count == 0)
        {
            return (steps, warnings);
        }

        var order = TableDependencyGraph.Build(session.Source.Tables).OrderForData();
        var ranked = order.Ordered.Concat(order.Cyclic).ToList();

        var selected = ranked
            .Where(session.DataSelections.ContainsKey)
            .Select(table => (Table: table, Selection: session.DataSelections[table]))
            .ToList();

        var emitted = new List<(ObjectIdentity Table, IReadOnlyList<ScriptStep> Steps)>();

        foreach (var (table, selection) in selected)
        {
            var changes = await data.ChangesAsync(session, table, selection, cancellationToken).ConfigureAwait(false);
            if (changes is null)
            {
                continue;
            }

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

            emitted.Add((table, _provider.CreateDataScriptEmitter().Emit(changes)));
        }

        foreach (var (_, produced) in Enumerable.Reverse(emitted))
        {
            steps.AddRange(produced.Where(s => s.Phase == ScriptPhase.DataDeletes));
        }

        foreach (var (_, produced) in emitted)
        {
            steps.AddRange(produced.Where(s => s.Phase == ScriptPhase.DataUpserts));
        }

        return (steps, warnings);
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
