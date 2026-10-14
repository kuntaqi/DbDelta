using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

// Turns a schema diff into an ordered PostgreSQL DDL script. Mirrors TSqlEmitter's phase model — the
// ScriptPhase enum, SyncScript and ScriptStep are engine-neutral — but the statements are PostgreSQL's and
// the alteration path is simpler, because PostgreSQL applies ALTER sub-commands independently and sees a new
// column immediately, so none of the batch-recompile bracketing T-SQL needs applies here.
//
// Scope (increment: the write path, schema half): create/drop/alter for tables, columns, keys, indexes,
// checks, foreign keys, sequences, types and programmables. The genuinely hard cases — a column change that
// would rewrite the table (identity/generated/most type changes beyond a widening), an ALTER to a type's own
// shape, and an expression index whose text cannot be reconstructed from the catalog safely — are refused
// into SyncScript.Refusals rather than emitted wrongly. Data rows are a separate increment.
public sealed class PostgreSqlScriptEmitter : IScriptEmitter
{
    public SyncScript Emit(
        DatabaseSchema source,
        DatabaseSchema target,
        SchemaDiff diff,
        IReadOnlySet<ObjectIdentity>? include = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(diff);

        var steps = new List<ScriptStep>();
        var refusals = new List<string>();
        var changes = diff.Differing
            .Where(o => include is null || include.Contains(o.Identity))
            .ToList();

        var sourceTables = source.Tables.ToDictionary(t => t.Identity);
        var targetTables = target.Tables.ToDictionary(t => t.Identity);

        EmitSchemas(steps, changes);
        EmitTypes(steps, refusals, changes, source);
        EmitSequences(steps, changes, source);
        EmitDrops(steps, changes, target);
        EmitTableCreations(steps, refusals, changes, sourceTables);
        EmitTableAlterations(steps, refusals, changes, sourceTables, targetTables);
        EmitProgrammables(steps, changes, source, target);

        return new SyncScript
        {
            Header = $"-- DbDelta · {source.DatabaseName} -> {target.DatabaseName}\n"
                + $"-- {changes.Count} object(s) changed",
            Steps = steps.OrderBy(s => s.Phase).ToList(),
            Refusals = refusals,
            // PostgreSQL rejects the T-SQL SET statements; the transaction keywords in ToSql are valid as-is.
            SessionSettings = [],
        };
    }

    private static void EmitSchemas(List<ScriptStep> steps, List<ObjectDiff> changes)
    {
        var schemas = changes
            .Where(c => c.Kind == DiffKind.SourceOnly)
            .Select(c => c.Identity.Schema)
            .Where(s => !string.Equals(s, "public", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var schema in schemas)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.Schemas, $"ensure schema {schema}", PgStatementWriter.CreateSchema(schema)));
        }
    }

    // Create and drop only, like the SQL Server side: there is no safe general ALTER for a domain's base type
    // or a composite's/enum's shape here, so a changed type is refused rather than dropped and rebuilt under
    // columns that use it.
    private static void EmitTypes(
        List<ScriptStep> steps, List<string> refusals, List<ObjectDiff> changes, DatabaseSchema source)
    {
        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.UserDefinedType))
        {
            if (change.Kind == DiffKind.TargetOnly)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DropTypes, $"drop type {change.Identity.QualifiedName}",
                    $"DROP TYPE {PostgreSqlQuoter.Instance.Qualify(change.Identity)};", Destructive: true));
                continue;
            }

            if (change.Kind == DiffKind.Different)
            {
                refusals.Add(
                    $"{change.Identity.QualifiedName}: the type differs, and this tool does not ALTER a "
                    + "user-defined type's own shape — dropping and recreating it would take down the columns "
                    + "that use it. Nothing is emitted for it.");
                continue;
            }

            var type = source.UserDefinedTypes.FirstOrDefault(t => t.Identity == change.Identity);
            if (type is not null && CreateType(type) is { } sql)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.CreateTypes, $"create type {change.Identity.QualifiedName}", sql));
            }
        }
    }

    private static string? CreateType(UserDefinedTypeDefinition type)
    {
        var name = PostgreSqlQuoter.Instance.Qualify(type.Identity);

        return type.Kind switch
        {
            UserDefinedTypeKind.Enum =>
                $"CREATE TYPE {name} AS ENUM ({string.Join(", ", type.EnumLabels.Select(Literal))});",
            UserDefinedTypeKind.Table =>
                $"CREATE TYPE {name} AS (\n"
                + string.Join(",\n", type.Columns.OrderBy(c => c.OrdinalPosition)
                    .Select(c => "    " + PostgreSqlQuoter.Instance.Quote(c.Name) + " " + PgTypeText.Declare(c.DataType)))
                + "\n);",
            UserDefinedTypeKind.Alias =>
                $"CREATE DOMAIN {name} AS {PgTypeText.Declare(type.BaseType!)}"
                + (type.IsNullable ? string.Empty : " NOT NULL")
                + string.Concat(type.CheckConstraints.Select(c => $" CHECK {c.Expression}")) + ";",
            _ => null,
        };
    }

    private static void EmitSequences(List<ScriptStep> steps, List<ObjectDiff> changes, DatabaseSchema source)
    {
        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.Sequence))
        {
            var name = PostgreSqlQuoter.Instance.Qualify(change.Identity);

            if (change.Kind == DiffKind.TargetOnly)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DropSequences, $"drop sequence {change.Identity.QualifiedName}",
                    $"DROP SEQUENCE {name};", Destructive: true));
                continue;
            }

            var sequence = source.Sequences.FirstOrDefault(s => s.Identity == change.Identity);
            if (sequence is null)
            {
                continue;
            }

            if (change.Kind == DiffKind.SourceOnly)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.CreateSequences, $"create sequence {change.Identity.QualifiedName}",
                    $"CREATE SEQUENCE {name} AS {PgTypeText.Declare(sequence.DataType)} "
                    + $"START WITH {sequence.StartValue}\n{SequenceOptions(sequence)};"));
                continue;
            }

            // ALTER SEQUENCE can move the increment, bounds and cycling but not the type; a type change is
            // reported rather than emitted. Where the sequence has got to is deliberately never touched.
            if (change.Properties.Any(p => p.Property == "DataType"))
            {
                continue;
            }

            steps.Add(new ScriptStep(
                ScriptPhase.CreateSequences, $"alter sequence {change.Identity.QualifiedName}",
                $"ALTER SEQUENCE {name}\n{SequenceOptions(sequence)};"));
        }
    }

    private static string SequenceOptions(SequenceDefinition sequence)
    {
        var options = new List<string> { $"    INCREMENT BY {sequence.Increment}" };
        options.Add(sequence.MinValue is { } min ? $"    MINVALUE {min}" : "    NO MINVALUE");
        options.Add(sequence.MaxValue is { } max ? $"    MAXVALUE {max}" : "    NO MAXVALUE");
        options.Add(sequence.IsCycling ? "    CYCLE" : "    NO CYCLE");
        return string.Join("\n", options);
    }

    private static void EmitDrops(List<ScriptStep> steps, List<ObjectDiff> changes, DatabaseSchema target)
    {
        var droppedTables = changes
            .Where(c => c.Kind == DiffKind.TargetOnly && c.Identity.Type == ObjectType.Table)
            .Select(c => c.Identity)
            .ToHashSet();

        // A table cannot be dropped while another points at it, so inbound foreign keys go first.
        foreach (var table in target.Tables)
        {
            foreach (var fk in table.ForeignKeys.Where(f => droppedTables.Contains(f.ReferencedTable)))
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DropForeignKeys,
                    $"drop {fk.Name} so {fk.ReferencedTable.QualifiedName} can be dropped",
                    PgStatementWriter.DropConstraint(table.Identity, fk.Name)));
            }
        }

        foreach (var change in changes.Where(c => c.Kind == DiffKind.TargetOnly && c.Identity.Type == ObjectType.Table))
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DropTables, $"drop table {change.Identity.QualifiedName}",
                PgStatementWriter.DropTable(change.Identity), Destructive: true));
        }
    }

    private static void EmitTableCreations(
        List<ScriptStep> steps, List<string> refusals, List<ObjectDiff> changes,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables)
    {
        foreach (var change in changes.Where(c => c.Kind == DiffKind.SourceOnly && c.Identity.Type == ObjectType.Table))
        {
            if (!sourceTables.TryGetValue(change.Identity, out var table))
            {
                continue;
            }

            steps.Add(new ScriptStep(
                ScriptPhase.CreateTables, $"create table {table.Identity.QualifiedName}",
                PgStatementWriter.CreateTable(table)));

            if (table.PrimaryKey is { } pk)
            {
                steps.Add(new ScriptStep(ScriptPhase.Keys, $"primary key {pk.Name}",
                    PgStatementWriter.AddPrimaryKey(table.Identity, pk)));
            }

            foreach (var unique in table.UniqueConstraints)
            {
                steps.Add(new ScriptStep(ScriptPhase.Keys, $"unique constraint {unique.Name}",
                    PgStatementWriter.AddUniqueConstraint(table.Identity, unique)));
            }

            foreach (var index in table.Indexes)
            {
                AddIndexStep(steps, refusals, table.Identity, index);
            }

            foreach (var check in table.CheckConstraints)
            {
                steps.Add(new ScriptStep(ScriptPhase.CheckConstraints, $"check {check.Name}",
                    PgStatementWriter.AddCheckConstraint(table.Identity, check)));
            }

            foreach (var fk in table.ForeignKeys)
            {
                steps.Add(new ScriptStep(ScriptPhase.AddForeignKeys, $"foreign key {fk.Name}",
                    PgStatementWriter.AddForeignKey(table.Identity, fk)));
            }
        }
    }

    private static void EmitTableAlterations(
        List<ScriptStep> steps, List<string> refusals, List<ObjectDiff> changes,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables,
        Dictionary<ObjectIdentity, TableDefinition> targetTables)
    {
        foreach (var change in changes.Where(c => c.Kind == DiffKind.Different && c.Identity.Type == ObjectType.Table))
        {
            if (!sourceTables.TryGetValue(change.Identity, out var source)
                || !targetTables.TryGetValue(change.Identity, out var target))
            {
                continue;
            }

            foreach (var child in change.DifferingChildren)
            {
                switch (child.Identity.Type)
                {
                    case ObjectType.Column:
                        EmitColumnChange(steps, refusals, change.Identity, child, source, target);
                        break;
                    case ObjectType.PrimaryKey:
                        EmitKeyChange(steps, change.Identity, child,
                            source.PrimaryKey is { } pk ? PgStatementWriter.AddPrimaryKey(change.Identity, pk) : null,
                            target.PrimaryKey?.Name, ScriptPhase.Keys);
                        break;
                    case ObjectType.UniqueConstraint:
                        EmitKeyChange(steps, change.Identity, child,
                            Lookup(source.UniqueConstraints, child.Identity.Name, u => u.Name) is { } su
                                ? PgStatementWriter.AddUniqueConstraint(change.Identity, su) : null,
                            Lookup(target.UniqueConstraints, child.Identity.Name, u => u.Name)?.Name, ScriptPhase.Keys);
                        break;
                    case ObjectType.CheckConstraint:
                        EmitKeyChange(steps, change.Identity, child,
                            Lookup(source.CheckConstraints, child.Identity.Name, c => c.Name) is { } sc
                                ? PgStatementWriter.AddCheckConstraint(change.Identity, sc) : null,
                            Lookup(target.CheckConstraints, child.Identity.Name, c => c.Name)?.Name,
                            ScriptPhase.CheckConstraints);
                        break;
                    case ObjectType.Index:
                        EmitIndexChange(steps, refusals, change.Identity, child, source, target);
                        break;
                    case ObjectType.ForeignKey:
                        EmitForeignKeyChange(steps, change.Identity, child, source, target);
                        break;
                    default:
                        break;
                }
            }
        }
    }

    private static void EmitColumnChange(
        List<ScriptStep> steps, List<string> refusals, ObjectIdentity table, ObjectDiff child,
        TableDefinition source, TableDefinition target)
    {
        var name = child.Identity.Name;

        if (child.Kind == DiffKind.SourceOnly)
        {
            steps.Add(new ScriptStep(ScriptPhase.AlterColumns, $"add column {table.QualifiedName}.{name}",
                PgStatementWriter.AddColumn(table, source.Columns.First(c => c.Name == name))));
            return;
        }

        if (child.Kind == DiffKind.TargetOnly)
        {
            steps.Add(new ScriptStep(ScriptPhase.AlterColumns, $"drop column {table.QualifiedName}.{name}",
                PgStatementWriter.DropColumn(table, name), Destructive: true));
            return;
        }

        var src = source.Columns.First(c => c.Name == name);
        var tgt = target.Columns.First(c => c.Name == name);

        // Identity and generated-stored changes rewrite the column; no single ALTER expresses them.
        if (!Equals(src.Identity, tgt.Identity) || src.ComputedExpression != tgt.ComputedExpression)
        {
            refusals.Add(
                $"{table.QualifiedName}.{name}: an identity or generated-column change needs the column "
                + "rebuilt, which is not done here. Nothing is emitted for it.");
            return;
        }

        if (!src.DataType.Equals(tgt.DataType))
        {
            steps.Add(new ScriptStep(ScriptPhase.AlterColumns, $"alter type {table.QualifiedName}.{name}",
                PgStatementWriter.AlterColumnType(table, src)));
        }

        if (src.IsNullable != tgt.IsNullable)
        {
            steps.Add(new ScriptStep(ScriptPhase.AlterColumns, $"nullability {table.QualifiedName}.{name}",
                PgStatementWriter.SetNotNull(table, name, !src.IsNullable)));
        }

        if (src.DefaultExpression != tgt.DefaultExpression)
        {
            steps.Add(new ScriptStep(ScriptPhase.AlterColumns, $"default {table.QualifiedName}.{name}",
                PgStatementWriter.SetDefault(table, src)));
        }
    }

    private static void EmitKeyChange(
        List<ScriptStep> steps, ObjectIdentity table, ObjectDiff child, string? addSql, string? targetName,
        ScriptPhase phase)
    {
        // A changed or target-only constraint is dropped by the name it has on the target; a changed or
        // source-only one is added from the source. A Different one does both.
        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different && targetName is not null)
        {
            steps.Add(new ScriptStep(phase, $"drop constraint {targetName}",
                PgStatementWriter.DropConstraint(table, targetName)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different && addSql is not null)
        {
            steps.Add(new ScriptStep(phase, $"add constraint {child.Identity.Name}", addSql));
        }
    }

    private static void EmitIndexChange(
        List<ScriptStep> steps, List<string> refusals, ObjectIdentity table, ObjectDiff child,
        TableDefinition source, TableDefinition target)
    {
        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different
            && Lookup(target.Indexes, child.Identity.Name, i => i.Name) is { } drop)
        {
            steps.Add(new ScriptStep(ScriptPhase.Indexes, $"drop index {drop.Name}",
                PgStatementWriter.DropIndex(table, drop.Name)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different
            && Lookup(source.Indexes, child.Identity.Name, i => i.Name) is { } add)
        {
            AddIndexStep(steps, refusals, table, add);
        }
    }

    private static void EmitForeignKeyChange(
        List<ScriptStep> steps, ObjectIdentity table, ObjectDiff child,
        TableDefinition source, TableDefinition target)
    {
        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different
            && Lookup(target.ForeignKeys, child.Identity.Name, f => f.Name) is { } drop)
        {
            steps.Add(new ScriptStep(ScriptPhase.DropForeignKeys, $"drop foreign key {drop.Name}",
                PgStatementWriter.DropConstraint(table, drop.Name)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different
            && Lookup(source.ForeignKeys, child.Identity.Name, f => f.Name) is { } add)
        {
            steps.Add(new ScriptStep(ScriptPhase.AddForeignKeys, $"add foreign key {add.Name}",
                PgStatementWriter.AddForeignKey(table, add)));
        }
    }

    private static void AddIndexStep(
        List<ScriptStep> steps, List<string> refusals, ObjectIdentity table, IndexDefinition index)
    {
        if (PgStatementWriter.IsExpressionIndex(index))
        {
            refusals.Add(
                $"{table.QualifiedName}.{index.Name}: an expression index cannot be reconstructed from the "
                + "catalog columns alone without risking a different formatting; nothing is emitted for it.");
            return;
        }

        steps.Add(new ScriptStep(ScriptPhase.Indexes, $"index {index.Name}",
            PgStatementWriter.CreateIndex(table, index)));
    }

    private static void EmitProgrammables(
        List<ScriptStep> steps, List<ObjectDiff> changes, DatabaseSchema source, DatabaseSchema target)
    {
        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.View))
        {
            var sourceView = source.Views.FirstOrDefault(v => v.Identity == change.Identity);
            var targetView = target.Views.FirstOrDefault(v => v.Identity == change.Identity);

            if (change.Kind is DiffKind.TargetOnly or DiffKind.Different && targetView is not null)
            {
                // A plain view is replaced in place; a materialised one has no CREATE OR REPLACE, so it is
                // dropped first. A target-only view is just dropped.
                if (change.Kind == DiffKind.TargetOnly || IsMaterialized(targetView))
                {
                    steps.Add(new ScriptStep(ScriptPhase.DropProgrammables, $"drop view {change.Identity.QualifiedName}",
                        PgStatementWriter.DropView(change.Identity, IsMaterialized(targetView))));
                }
            }

            if (change.Kind is DiffKind.SourceOnly or DiffKind.Different && sourceView is not null)
            {
                steps.Add(new ScriptStep(ScriptPhase.Programmables, $"create view {change.Identity.QualifiedName}",
                    PgStatementWriter.CreateView(sourceView, IsMaterialized(sourceView))));
            }
        }

        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.Routine))
        {
            if (change.Kind == DiffKind.TargetOnly)
            {
                var routine = target.Routines.First(r => r.Identity == change.Identity);
                steps.Add(new ScriptStep(ScriptPhase.DropProgrammables, $"drop routine {change.Identity.QualifiedName}",
                    PgStatementWriter.DropRoutine(routine)));
                continue;
            }

            var sourceRoutine = source.Routines.FirstOrDefault(r => r.Identity == change.Identity);
            if (sourceRoutine is not null)
            {
                steps.Add(new ScriptStep(ScriptPhase.Programmables, $"create routine {change.Identity.QualifiedName}",
                    PgStatementWriter.CreateRoutine(sourceRoutine)));
            }
        }

        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.Trigger))
        {
            var targetTrigger = target.Triggers.FirstOrDefault(t => t.Identity == change.Identity);
            if (change.Kind is DiffKind.TargetOnly or DiffKind.Different && targetTrigger is not null)
            {
                steps.Add(new ScriptStep(ScriptPhase.DropProgrammables, $"drop trigger {change.Identity.QualifiedName}",
                    PgStatementWriter.DropTrigger(targetTrigger, BareTriggerName(targetTrigger))));
            }

            var sourceTrigger = source.Triggers.FirstOrDefault(t => t.Identity == change.Identity);
            if (change.Kind is DiffKind.SourceOnly or DiffKind.Different && sourceTrigger is not null)
            {
                steps.Add(new ScriptStep(ScriptPhase.Programmables, $"create trigger {change.Identity.QualifiedName}",
                    PgStatementWriter.CreateTrigger(sourceTrigger)));
            }
        }
    }

    private static bool IsMaterialized(ViewDefinition view) =>
        string.Equals(view.Extras["Materialized"], "true", StringComparison.Ordinal);

    // The trigger identity name is "{table}.{trigger}"; the bare trigger name is what DROP TRIGGER needs.
    private static string BareTriggerName(TriggerDefinition trigger) =>
        trigger.Identity.Name.StartsWith(trigger.Table.Name + ".", StringComparison.Ordinal)
            ? trigger.Identity.Name[(trigger.Table.Name.Length + 1)..]
            : trigger.Identity.Name;

    private static T? Lookup<T>(IEnumerable<T> items, string name, Func<T, string> nameOf) where T : class =>
        items.FirstOrDefault(i => string.Equals(nameOf(i), name, StringComparison.OrdinalIgnoreCase));

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
