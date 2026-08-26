using System.Diagnostics;
using DbDelta.Api.Contracts;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
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
        var connectionString = _connections.Build(request);
        var info = await _provider.ProbeAsync(connectionString, cancellationToken).ConfigureAwait(false);
        var schema = await _provider.CreateSchemaReader(connectionString).ReadAsync(cancellationToken).ConfigureAwait(false);

        return new ProbeResponse(
            request.Server,
            info.DatabaseName,
            info.ProductVersion,
            info.Edition,
            info.Collation,
            _classifier.Classify(request.Server),
            _classifier.IsReadOnly(request.Server),
            schema.Tables.Count,
            schema.Views.Count,
            schema.Routines.Count);
    }

    public async Task<CompareResponse> CompareAsync(CompareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceConnection = _connections.Build(request.Source);
        var targetConnection = _connections.Build(request.Target);

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
            SourceServer = request.Source.Server,
            TargetServer = request.Target.Server,
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
            CollationWarning(session),
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

    public ScriptResponse Script(CompareSession session, IReadOnlyList<string> include)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(include);

        var identities = include.Count == 0
            ? session.Diff.Differing.Select(o => o.Identity).ToHashSet()
            : include.Select(session.Resolve).OfType<ObjectIdentity>().ToHashSet();

        var script = new TSqlEmitterAdapter(_provider).Emit(session, identities);
        var sql = script.ToSql();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(sql);

        return new ScriptResponse(
            sql,
            script.Count,
            bytes,
            bytes > _safety.MaxReviewableScriptBytes,
            script.Steps.Select(s => new StepDto(s.Phase.ToString(), s.Description, s.Sql)).ToList());
    }

    private string? CollationWarning(CompareSession session) =>
        string.Equals(session.Source.Collation, session.Target.Collation, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Source is {session.Source.Collation} and target is {session.Target.Collation}. "
                + "String comparison differs between them, so data compare results cannot be trusted until this is resolved.";

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
