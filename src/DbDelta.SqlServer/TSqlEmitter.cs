using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer;

public sealed class TSqlEmitter : IScriptEmitter
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
        var changes = diff.Differing
            .Where(o => include is null || include.Contains(o.Identity))
            .ToList();

        var sourceTables = source.Tables.ToDictionary(t => t.Identity);
        var targetTables = target.Tables.ToDictionary(t => t.Identity);

        // A column added by ALTER TABLE is not visible to later statements in the same batch: SQL
        // Server compiles the whole batch up front, so CREATE INDEX on a just-added column fails with
        // "Invalid column name". Statements touching these tables run as their own batch instead.
        var deferred = changes
            .Where(c => c.Kind == DiffKind.Different && c.Identity.Type == ObjectType.Table)
            .Where(c => c.DifferingChildren.Any(ch =>
                ch.Identity.Type == ObjectType.Column && ch.Kind == DiffKind.SourceOnly))
            .Select(c => c.Identity)
            .ToHashSet();

        // A type created by this script is not visible to a statement compiled in the same batch: SQL
        // Server resolves data types when it compiles, not when it runs. Whatever uses one has to be its
        // own batch, which is the same reason CREATE VIEW is wrapped further down.
        var newTypes = changes
            .Where(c => c.Kind == DiffKind.SourceOnly && c.Identity.Type == ObjectType.UserDefinedType)
            .Select(c => c.Identity)
            .ToHashSet();

        var refusals = new List<string>();
        var context = new EmitContext(source, target, changes, deferred, steps, refusals);

        EmitSchemas(steps, changes);
        EmitDrops(steps, changes, target);
        EmitTypes(steps, changes, source, target);
        EmitSequences(steps, changes, source);
        EmitTableCreations(steps, changes, sourceTables, newTypes, target.Collation, refusals);
        EmitTableAlterations(context, changes, sourceTables, targetTables);
        EmitProgrammables(steps, changes, source);
        steps.AddRange(context.Trailing);

        return new SyncScript
        {
            Header = $"-- DbDelta · {source.DatabaseName} -> {target.DatabaseName}\n"
                + $"-- {changes.Count} object(s) changed",
            Steps = steps.OrderBy(s => s.Phase).ToList(),
            Refusals = refusals
        };
    }

    // One place decides whether an index can be written, so the answer cannot depend on which of the three
    // paths reached it: creating a table, rebuilding the indexes a changed column depended on, or a
    // changed index. A refusal is carried out of the script rather than emitted as a comment, because a
    // step that runs and does nothing would count as committed.
    internal static void AddIndexStep(
        List<ScriptStep> steps,
        List<string> refusals,
        ObjectIdentity table,
        IndexDefinition index,
        string description,
        bool defer)
    {
        if (IndexEmitSupport.Refusal(table, index) is { } refusal)
        {
            refusals.Add(refusal);
            return;
        }

        steps.Add(new ScriptStep(
            IndexEmitSupport.IsSecondary(index) ? ScriptPhase.SecondaryIndexes : ScriptPhase.Indexes,
            description,
            Batch(TSqlWriter.CreateIndex(table, index), defer)));
    }

    // Only the schemas this script actually creates something in, which is the schema of every object it
    // creates and nothing else. An object that merely differs already exists on the target, so its schema
    // does too; an object being dropped needs no schema at all.
    //
    // This used to add every schema in the source database as well, on top of the ones the plan needed.
    // Nothing broke — CreateSchemaIfMissing is guarded and idempotent — but a plan of one dbo table would
    // carry a CREATE SCHEMA for schemas nothing in it referred to, which is the one thing this tool is
    // strictest about not doing. The whole-source list arrived with the first version of the emitter and
    // was never a fix for anything; no test asserted on schema steps, so it went unnoticed for as long as
    // it existed.
    private static void EmitSchemas(List<ScriptStep> steps, List<ObjectDiff> changes)
    {
        var schemas = changes
            .Where(c => c.Kind == DiffKind.SourceOnly)
            .Select(c => c.Identity.Schema)
            .Where(s => !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in schemas)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.Schemas,
                $"ensure schema {schema}",
                TSqlWriter.CreateSchemaIfMissing(schema)));
        }
    }

    // Creates and drops only. There is no ALTER TYPE in T-SQL, so a type that differs is left alone and
    // said so about elsewhere — emitting a DROP and CREATE would fail the moment a column used it, and
    // succeeding would mean this tool had silently dropped and rebuilt those columns.
    private static void EmitTypes(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        DatabaseSchema source,
        DatabaseSchema target)
    {
        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.UserDefinedType))
        {
            if (change.Kind == DiffKind.TargetOnly)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DropTypes,
                    $"drop type {change.Identity.QualifiedName}",
                    $"DROP TYPE {SqlServerQuoter.Instance.Qualify(change.Identity)};",
                    Destructive: true));

                continue;
            }

            if (change.Kind != DiffKind.SourceOnly)
            {
                continue;
            }

            var type = source.UserDefinedTypes.FirstOrDefault(t => t.Identity == change.Identity);

            // A CLR type is a name for something living in an assembly this tool never reads and could
            // not install. Emitting CREATE TYPE for one would produce a statement that cannot work.
            if (type is null || type.Kind == UserDefinedTypeKind.Clr)
            {
                continue;
            }

            steps.Add(new ScriptStep(
                ScriptPhase.CreateTypes,
                $"create type {change.Identity.QualifiedName}",
                CreateType(type, target.Collation)));
        }
    }

    private static string CreateType(UserDefinedTypeDefinition type, string? databaseCollation)
    {
        var name = SqlServerQuoter.Instance.Qualify(type.Identity);

        if (type.Kind == UserDefinedTypeKind.Table)
        {
            var quoter = SqlServerQuoter.Instance;

            // Nothing here carries a constraint name, because none of it can: the syntax has no place to
            // put one. Passing the names through would produce a statement SQL Server refuses to parse.
            var parts = type.Columns
                .OrderBy(c => c.OrdinalPosition)
                .Select(c => "    " + TSqlWriter.ColumnDefinition(c, includeDefault: false, databaseCollation)
                    + (c.DefaultExpression is null ? string.Empty : $" DEFAULT {c.DefaultExpression}"))
                .ToList();

            if (type.PrimaryKey is { } pk)
            {
                parts.Add($"    PRIMARY KEY {Clustering(pk.IsClustered)} ({Columns(pk.Columns)})");
            }

            parts.AddRange(type.UniqueConstraints
                .Select(u => $"    UNIQUE {Clustering(u.IsClustered)} ({Columns(u.Columns)})"));

            parts.AddRange(type.CheckConstraints.Select(c => $"    CHECK {c.Expression}"));

            parts.AddRange(type.Indexes.Select(i =>
                $"    INDEX {quoter.Quote(i.Name)} {(i.IsUnique ? "UNIQUE " : string.Empty)}"
                + $"{Clustering(i.IsClustered)} ({Columns(i.Columns)})"
                + (i.IncludedColumns.Count == 0
                    ? string.Empty
                    : $" INCLUDE ({string.Join(", ", i.IncludedColumns.Select(quoter.Quote))})")));

            return $"CREATE TYPE {name} AS TABLE (\n{string.Join(",\n", parts)}\n);";
        }

        var nullability = type.IsNullable ? "NULL" : "NOT NULL";

        return $"CREATE TYPE {name} FROM {SqlTypeText.Declare(type.BaseType!)} {nullability};";
    }

    // Sequences were read and compared long before anything emitted them, so a database with one produced a
    // plan that listed it and a script that contained nothing for it. Silently.
    private static void EmitSequences(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        DatabaseSchema source)
    {
        foreach (var change in changes.Where(c => c.Identity.Type == ObjectType.Sequence))
        {
            var name = SqlServerQuoter.Instance.Qualify(change.Identity);

            if (change.Kind == DiffKind.TargetOnly)
            {
                // Destructive: dropping a sequence loses where it had got to, and nothing brings that back.
                steps.Add(new ScriptStep(
                    ScriptPhase.DropSequences,
                    $"drop sequence {change.Identity.QualifiedName}",
                    $"DROP SEQUENCE {name};",
                    Destructive: true));

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
                    ScriptPhase.CreateSequences,
                    $"create sequence {change.Identity.QualifiedName}",
                    $"CREATE SEQUENCE {name}\n    AS {SqlTypeText.Declare(sequence.DataType)}\n"
                        + $"    START WITH {sequence.StartValue}\n{Options(sequence)};"));

                continue;
            }

            // ALTER SEQUENCE can move the increment, the bounds and the cycling, and cannot change the type.
            // A type difference is therefore reported rather than emitted, the same as a user-defined type.
            //
            // What it deliberately never touches is where the sequence has got to. RESTART WITH exists and
            // would make the target's current value match the source's start value — which on a live
            // sequence means handing out numbers it has already issued.
            if (change.Properties.Any(p => p.Property == "DataType"))
            {
                continue;
            }

            steps.Add(new ScriptStep(
                ScriptPhase.CreateSequences,
                $"alter sequence {change.Identity.QualifiedName}",
                $"ALTER SEQUENCE {name}\n{Options(sequence)};"));
        }
    }

    private static string Options(SequenceDefinition sequence)
    {
        var options = new List<string> { $"    INCREMENT BY {sequence.Increment}" };

        options.Add(sequence.MinValue is { } min ? $"    MINVALUE {min}" : "    NO MINVALUE");
        options.Add(sequence.MaxValue is { } max ? $"    MAXVALUE {max}" : "    NO MAXVALUE");
        options.Add(sequence.IsCycling ? "    CYCLE" : "    NO CYCLE");

        return string.Join("\n", options);
    }

    private static string Clustering(bool clustered) => clustered ? "CLUSTERED" : "NONCLUSTERED";

    private static string Columns(IEnumerable<IndexColumn> columns) =>
        string.Join(", ", columns.Select(c =>
            $"{SqlServerQuoter.Instance.Quote(c.Name)} {(c.IsDescending ? "DESC" : "ASC")}"));

    private static void EmitDrops(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        DatabaseSchema target)
    {
        var droppedTables = changes
            .Where(c => c.Kind == DiffKind.TargetOnly && c.Identity.Type == ObjectType.Table)
            .Select(c => c.Identity)
            .ToHashSet();

        // A table cannot be dropped while another table still points at it, so inbound keys go first.
        foreach (var table in target.Tables)
        {
            foreach (var fk in table.ForeignKeys.Where(f => droppedTables.Contains(f.ReferencedTable)))
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.DropForeignKeys,
                    $"drop {fk.Name} so {fk.ReferencedTable.QualifiedName} can be dropped",
                    TSqlWriter.DropConstraint(table.Identity, fk.Name)));
            }
        }

        foreach (var change in changes.Where(c => c.Kind == DiffKind.TargetOnly))
        {
            switch (change.Identity.Type)
            {
                case ObjectType.Table:
                    steps.Add(new ScriptStep(
                        ScriptPhase.DropTables,
                        $"drop table {change.Identity.QualifiedName}",
                        TSqlWriter.DropTable(change.Identity),
                        Destructive: true));
                    break;

                case ObjectType.View:
                    steps.Add(new ScriptStep(
                        ScriptPhase.DropProgrammables,
                        $"drop view {change.Identity.QualifiedName}",
                        TSqlWriter.DropProgrammable(change.Identity, "VIEW")));
                    break;

                case ObjectType.Routine:
                    steps.Add(new ScriptStep(
                        ScriptPhase.DropProgrammables,
                        $"drop routine {change.Identity.QualifiedName}",
                        TSqlWriter.DropProgrammable(change.Identity, RoutineKeyword(target, change.Identity))));
                    break;

                case ObjectType.Trigger:
                    steps.Add(new ScriptStep(
                        ScriptPhase.DropProgrammables,
                        $"drop trigger {change.Identity.QualifiedName}",
                        TSqlWriter.DropProgrammable(change.Identity, "TRIGGER")));
                    break;

                default:
                    break;
            }
        }

    }

    private static void EmitTableCreations(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables,
        HashSet<ObjectIdentity> newTypes,
        string? databaseCollation,
        List<string> refusals)
    {
        foreach (var change in changes.Where(c => c.Kind == DiffKind.SourceOnly && c.Identity.Type == ObjectType.Table))
        {
            if (!sourceTables.TryGetValue(change.Identity, out var table))
            {
                continue;
            }

            steps.Add(new ScriptStep(
                ScriptPhase.CreateTables,
                $"create table {table.Identity.QualifiedName}",
                Batch(TSqlWriter.CreateTable(table, databaseCollation), UsesNewType(table, newTypes))));

            if (table.PrimaryKey is not null)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.Keys,
                    $"primary key {table.PrimaryKey.Name}",
                    TSqlWriter.AddPrimaryKey(table.Identity, table.PrimaryKey)));
            }

            foreach (var unique in table.UniqueConstraints)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.Keys,
                    $"unique constraint {unique.Name}",
                    TSqlWriter.AddUniqueConstraint(table.Identity, unique)));
            }

            foreach (var index in table.Indexes)
            {
                AddIndexStep(steps, refusals, table.Identity, index, $"index {index.Name}", defer: false);
            }

            foreach (var check in table.CheckConstraints)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.CheckConstraints,
                    $"check {check.Name}",
                    TSqlWriter.AddCheckConstraint(table.Identity, check)));
            }

            foreach (var fk in table.ForeignKeys)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.AddForeignKeys,
                    $"foreign key {fk.Name}",
                    TSqlWriter.AddForeignKey(table.Identity, fk)));
            }
        }
    }

    private static void EmitTableAlterations(
        EmitContext context,
        List<ObjectDiff> changes,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables,
        Dictionary<ObjectIdentity, TableDefinition> targetTables)
    {
        var tables = changes
            .Where(c => c.Kind == DiffKind.Different && c.Identity.Type == ObjectType.Table)
            .Where(c => sourceTables.ContainsKey(c.Identity) && targetTables.ContainsKey(c.Identity))
            .Select(c => (
                Change: c,
                Source: sourceTables[c.Identity],
                Target: targetTables[c.Identity],
                Columns: ColumnChange.From(c, sourceTables[c.Identity], targetTables[c.Identity])))
            .ToList();

        // Which tables are rebuilt is settled before anything is emitted, because it changes what every
        // other table may do with the foreign keys it shares with them.
        var blocked = new Dictionary<ObjectIdentity, string>();

        foreach (var table in tables.Where(t => t.Columns.Any(c => c.NeedsRebuild)))
        {
            if (TableRebuild.Blocker(table.Target) is { } reason)
            {
                blocked[table.Change.Identity] = reason;
                continue;
            }

            context.Rebuilt.Add(table.Change.Identity);
            context.Deferred.Add(table.Change.Identity);
        }

        foreach (var (change, source, target, columns) in tables)
        {
            if (context.Rebuilt.Contains(change.Identity))
            {
                TableRebuild.Emit(context, source, target, columns);
                continue;
            }

            EmitInPlace(context, change, source, target, columns, blocked.GetValueOrDefault(change.Identity));
        }
    }

    // Everything that can be done to the table where it stands. The shape of it is a bracket: whatever
    // holds a changing column comes down first, the columns change, and what came down goes back up —
    // from the source when it is part of this table, from the target when it belongs to another table.
    private static void EmitInPlace(
        EmitContext context,
        ObjectDiff change,
        TableDefinition source,
        TableDefinition target,
        IReadOnlyList<ColumnChange> columns,
        string? rebuildBlocker)
    {
        var table = change.Identity;
        var defer = context.Defers(table);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns.Where(c => c.NeedsRebuild))
        {
            context.Refusals.Add(
                $"{table.QualifiedName}.{column.Name}: {column.DescribeRebuildNeed()}. No ALTER can make that change — "
                + $"it takes rebuilding the table, which is not done here because {rebuildBlocker}. "
                + "Nothing is emitted for that part of the difference.");

            if (column.ComputedKindDiffers)
            {
                refused.Add(column.Name);
            }
        }

        foreach (var column in columns.Where(c => c.OrdinalOnly))
        {
            context.Refusals.Add(
                $"{table.QualifiedName}.{column.Name} is in a different position. Column order can only be changed "
                + "by rebuilding the table, which is not done for order alone, so nothing is emitted for it.");
        }

        // A schema-bound module holds the columns it reads, and dropping it is not this table's business:
        // it is an object of its own that the plan may not even include.
        foreach (var column in columns.Where(c => c.TouchesColumn && !refused.Contains(c.Name)))
        {
            var holders = target.SchemaBoundReferences
                .Where(r => r.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
                .Select(r => r.Module.QualifiedName)
                .Order()
                .ToList();

            if (holders.Count > 0)
            {
                refused.Add(column.Name);
                context.Refusals.Add(
                    $"{table.QualifiedName}.{column.Name} cannot be {(column.IsDrop ? "dropped" : "altered")} while "
                    + $"{string.Join(", ", holders)} {(holders.Count == 1 ? "is" : "are")} schema-bound to it. "
                    + "Nothing is emitted for that column.");
            }
        }

        var touched = columns
            .Where(c => c.TouchesColumn && !refused.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();

        // Keys this table's own changes drop, which take the foreign keys that reference them down too.
        var droppedKeys = change.DifferingChildren
            .Where(c => c.Kind is DiffKind.TargetOnly or DiffKind.Different)
            .Select(c => KeyColumns(target, c))
            .OfType<IReadOnlyList<string>>()
            .ToList();

        var dependents = ColumnDependents.Find(context.Target, target, touched, droppedKeys);

        EmitDependentDrops(context, table, source, dependents, columns, handled);

        foreach (var column in columns.Where(c => !refused.Contains(c.Name)))
        {
            EmitColumn(context, table, column, handled);
        }

        EmitDependentRestores(context, table, source, dependents, columns, defer);

        foreach (var child in change.DifferingChildren)
        {
            switch (child.Identity.Type)
            {
                case ObjectType.Index:
                    EmitIndexChange(context.Steps, child, table, source, handled, defer, context.Refusals);
                    break;

                case ObjectType.CheckConstraint:
                    EmitCheckChange(context.Steps, child, table, source, handled, defer);
                    break;

                case ObjectType.ForeignKey:
                    EmitForeignKeyChange(context.Steps, child, table, source, handled, defer);
                    break;

                case ObjectType.PrimaryKey:
                case ObjectType.UniqueConstraint:
                    EmitKeyChange(context.Steps, child, table, source, handled, defer);
                    break;

                default:
                    break;
            }
        }
    }

    // The column list of a key or unique index that a child difference is about to drop, or null when
    // the child is something else. Only unique ones matter: nothing can reference any other index.
    private static IReadOnlyList<string>? KeyColumns(TableDefinition target, ObjectDiff child)
    {
        var name = child.Identity.Name;

        return child.Identity.Type switch
        {
            // A table has one primary key whatever it is called, and the child is named from the source side.
            ObjectType.PrimaryKey when target.PrimaryKey is { } pk => pk.Columns.Select(c => c.Name).ToList(),
            ObjectType.UniqueConstraint =>
                target.UniqueConstraints.FirstOrDefault(u => Same(u.Name, name))?.Columns.Select(c => c.Name).ToList(),
            ObjectType.Index =>
                target.Indexes.FirstOrDefault(i => i.IsUnique && Same(i.Name, name))?.Columns.Select(c => c.Name).ToList(),
            _ => null
        };
    }

    // Order matters twice over. Statistics and nonclustered structures go before the clustered one, so
    // dropping the clustered index does not rebuild them first only to see them dropped; and a computed
    // column goes last, because the indexes on it had to come down before it could.
    private static void EmitDependentDrops(
        EmitContext context,
        ObjectIdentity table,
        TableDefinition source,
        ColumnDependents dependents,
        IReadOnlyList<ColumnChange> columns,
        HashSet<string> handled)
    {
        var reason = $"it holds a column of {table.QualifiedName} that changes";

        foreach (var inbound in dependents.InboundKeys)
        {
            context.BracketInbound(inbound, $"it references a key or column of {table.QualifiedName} that changes");
        }

        // This table's own keys come back as the source has them. One its own difference changes is left to
        // that difference, which drops it and adds the source's by itself.
        foreach (var key in dependents.OutboundKeys)
        {
            context.BracketForeignKey(
                table,
                key.Name,
                source.ForeignKeys.FirstOrDefault(f => Same(f.Name, key.Name)),
                reason);
        }

        foreach (var statistics in dependents.Statistics.Where(s => handled.Add($"stats:{s.Name}")))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop statistics {statistics.Name} — {reason}",
                TSqlWriter.DropStatistics(table, statistics.Name)));
        }

        foreach (var index in dependents.Indexes.OrderBy(i => i.IsClustered).Where(i => handled.Add($"index:{i.Name}")))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop index {index.Name} — {reason}",
                TSqlWriter.DropIndex(table, index.Name)));
        }

        foreach (var check in dependents.Checks.Where(c => handled.Add($"check:{c.Name}")))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop check {check.Name} — {reason}",
                TSqlWriter.DropConstraint(table, check.Name)));
        }

        foreach (var column in dependents.Defaults.Where(c => handled.Add($"default:{c.Name}")))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop default {column.DefaultConstraintName} — {reason}",
                TSqlWriter.DropConstraint(table, column.DefaultConstraintName!)));
        }

        var keys = dependents.Uniques.Select(u => (u.Name, u.IsClustered)).ToList();

        if (dependents.PrimaryKey is { } pk)
        {
            keys.Add((pk.Name, pk.IsClustered));
        }

        foreach (var (name, _) in keys.OrderBy(k => k.IsClustered).Where(k => handled.Add($"key:{k.Name}")))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop constraint {name} — {reason}",
                TSqlWriter.DropConstraint(table, name)));
        }

        // The source may call its primary key something else, and the difference is named from that side.
        if (dependents.PrimaryKey is not null && source.PrimaryKey is { } renamed)
        {
            handled.Add($"key:{renamed.Name}");
        }

        // A computed column the plan drops for good is dropped by its own step; this is for the ones that
        // come back.
        foreach (var column in dependents.ComputedColumns.Where(c =>
            !columns.Any(ch => ch.IsDrop && Same(ch.Name, c.Name)) && handled.Add($"computed:{c.Name}")))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop computed column {table.QualifiedName}.{column.Name} — it is rebuilt",
                TSqlWriter.DropColumn(table, column.Name)));
        }
    }

    private static void EmitColumn(EmitContext context, ObjectIdentity table, ColumnChange column, HashSet<string> handled)
    {
        var name = column.Name;

        if (column.IsDrop)
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"drop column {table.QualifiedName}.{name}",
                TSqlWriter.DropColumn(table, name),
                Destructive: true));
            return;
        }

        if (column.Source is null)
        {
            return;
        }

        if (column.IsAdd)
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"add column {table.QualifiedName}.{name}",
                TSqlWriter.AddColumn(table, column.Source, context.Collation)));
            return;
        }

        // A default can change with nothing else about the column, and no ALTER COLUMN says anything about
        // defaults — so a default-only difference used to emit an ALTER that changed nothing at all.
        if (column.DefaultDiffers
            && column.Target?.DefaultConstraintName is { } old
            && handled.Add($"default:{name}"))
        {
            context.Steps.Add(Step(ScriptPhase.AlterColumns, $"drop default {old} on {table.QualifiedName}.{name}",
                TSqlWriter.DropConstraint(table, old)));
        }

        if (!column.NeedsAlter)
        {
            return;
        }

        // A narrowing change is destructive even though it drops nothing: the server rounds DECIMAL scale
        // and DATETIME2 precision without complaint, so left unflagged it would rewrite existing rows and
        // report success. The verdict is type arithmetic over both sides, already known here.
        var narrowing = column.Target is null
            ? ColumnNarrowing.None
            : NarrowingAnalyzer.Analyse(column.Target, column.Source);

        context.Steps.Add(new ScriptStep(
            ScriptPhase.AlterColumns,
            narrowing.LosesData
                ? $"alter column {table.QualifiedName}.{name} — {narrowing.Reason}"
                : $"alter column {table.QualifiedName}.{name}",
            TSqlWriter.AlterColumn(table, column.Source, context.Collation),
            Destructive: narrowing.LosesData));
    }

    // What this table owns comes back as the source has it, since that is what the plan is making the table
    // into; what came down and has no counterpart on the source stays down, which is what its own
    // difference was asking for anyway. Statistics are the exception — they are not compared, so the
    // target's own are put back.
    private static void EmitDependentRestores(
        EmitContext context,
        ObjectIdentity table,
        TableDefinition source,
        ColumnDependents dependents,
        IReadOnlyList<ColumnChange> columns,
        bool defer)
    {
        foreach (var column in dependents.ComputedColumns.Concat(
            columns.Where(c => c.RecreatesComputed).Select(c => c.Target!)).DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (Find(source.Columns, column.Name) is { ComputedExpression: not null } replacement
                && !columns.Any(c => c.IsDrop && Same(c.Name, column.Name)))
            {
                context.Steps.Add(Step(ScriptPhase.AlterColumns, $"add computed column {table.QualifiedName}.{column.Name}",
                    Batch(TSqlWriter.AddColumn(table, replacement, context.Collation), defer)));
            }
        }

        var defaults = dependents.Defaults.Select(c => c.Name)
            .Concat(columns.Where(c => c.DefaultDiffers && !c.IsAdd && !c.IsDrop).Select(c => c.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in defaults)
        {
            if (Find(source.Columns, name) is { DefaultExpression: not null, DefaultConstraintName: not null } column
                && !columns.Any(c => c.IsDrop && Same(c.Name, name)))
            {
                context.Steps.Add(Step(ScriptPhase.AlterColumns, $"default {column.DefaultConstraintName} on {table.QualifiedName}.{name}",
                    Batch(TSqlWriter.AddDefault(table, column), defer)));
            }
        }

        foreach (var statistics in dependents.Statistics.Where(s => s.Columns.All(c => Find(source.Columns, c) is not null)))
        {
            context.Steps.Add(Step(ScriptPhase.Indexes, $"restore statistics {statistics.Name}",
                Batch(TSqlWriter.CreateStatistics(table, statistics), defer)));
        }

        foreach (var index in dependents.Indexes)
        {
            if (source.Indexes.FirstOrDefault(i => Same(i.Name, index.Name)) is { } replacement)
            {
                AddIndexStep(context.Steps, context.Refusals, table, replacement, $"recreate index {replacement.Name}", defer);
            }
        }

        foreach (var check in dependents.Checks)
        {
            if (source.CheckConstraints.FirstOrDefault(c => Same(c.Name, check.Name)) is { } replacement)
            {
                context.Steps.Add(Step(ScriptPhase.CheckConstraints, $"recreate check {replacement.Name}",
                    Batch(TSqlWriter.AddCheckConstraint(table, replacement), defer)));
            }
        }

        if (dependents.PrimaryKey is not null && source.PrimaryKey is { } pk)
        {
            context.Steps.Add(Step(ScriptPhase.Keys, $"recreate primary key {pk.Name}",
                Batch(TSqlWriter.AddPrimaryKey(table, pk), defer)));
        }

        foreach (var unique in dependents.Uniques)
        {
            if (source.UniqueConstraints.FirstOrDefault(u => Same(u.Name, unique.Name)) is { } replacement)
            {
                context.Steps.Add(Step(ScriptPhase.Keys, $"recreate unique constraint {replacement.Name}",
                    Batch(TSqlWriter.AddUniqueConstraint(table, replacement), defer)));
            }
        }
    }

    private static void EmitIndexChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        HashSet<string> handled,
        bool defer,
        List<string> refusals)
    {
        var name = child.Identity.Name;

        if (!handled.Add($"index:{name}"))
        {
            return;
        }

        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"drop index {name}",
                TSqlWriter.DropIndex(table, name)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different)
        {
            var index = source.Indexes.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index is not null)
            {
                AddIndexStep(steps, refusals, table, index, $"index {name}", defer);
            }
        }
    }

    private static void EmitCheckChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        HashSet<string> handled,
        bool defer)
    {
        var name = child.Identity.Name;

        if (!handled.Add($"check:{name}"))
        {
            return;
        }

        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.CheckConstraints,
                $"drop check {name}",
                TSqlWriter.DropConstraint(table, name)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different)
        {
            var check = source.CheckConstraints.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (check is not null)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.CheckConstraints,
                    $"check {name}",
                    Batch(TSqlWriter.AddCheckConstraint(table, check), defer)));
            }
        }
    }

    private static void EmitForeignKeyChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        HashSet<string> handled,
        bool defer)
    {
        var name = child.Identity.Name;

        if (!handled.Add($"fk:{name}"))
        {
            return;
        }

        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.DropForeignKeys,
                $"drop foreign key {name}",
                TSqlWriter.DropConstraint(table, name)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different)
        {
            var fk = source.ForeignKeys.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (fk is not null)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.AddForeignKeys,
                    $"foreign key {name}",
                    Batch(TSqlWriter.AddForeignKey(table, fk), defer)));
            }
        }
    }

    private static void EmitKeyChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        HashSet<string> handled,
        bool defer)
    {
        var name = child.Identity.Name;

        if (!handled.Add($"key:{name}"))
        {
            return;
        }

        if (child.Kind is DiffKind.TargetOnly or DiffKind.Different)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.Keys,
                $"drop constraint {name}",
                TSqlWriter.DropConstraint(table, name)));
        }

        if (child.Kind is DiffKind.SourceOnly or DiffKind.Different)
        {
            if (source.PrimaryKey is not null
                && string.Equals(source.PrimaryKey.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.Keys,
                    $"primary key {name}",
                    Batch(TSqlWriter.AddPrimaryKey(table, source.PrimaryKey), defer)));
                return;
            }

            var unique = source.UniqueConstraints
                .FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

            if (unique is not null)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.Keys,
                    $"unique constraint {name}",
                    Batch(TSqlWriter.AddUniqueConstraint(table, unique), defer)));
            }
        }
    }

    private static ScriptStep Step(ScriptPhase phase, string description, string sql) => new(phase, description, sql);

    private static ColumnDefinition? Find(IEnumerable<ColumnDefinition> columns, string name) =>
        columns.FirstOrDefault(c => Same(c.Name, name));

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void EmitProgrammables(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        DatabaseSchema source)
    {
        var programmables = changes
            .Where(c => c.Kind is DiffKind.SourceOnly or DiffKind.Different)
            .Where(c => c.Identity.Type is ObjectType.View or ObjectType.Routine or ObjectType.Trigger)
            .ToList();

        // Alphabetical order matched dependency order by luck in the first schema this ran against.
        // A view selecting from another view has to come after it, and only the catalog knows that.
        var dependsOn = source.Views.ToDictionary(v => v.Identity, v => v.DependsOn);
        foreach (var routine in source.Routines)
        {
            dependsOn[routine.Identity] = routine.DependsOn;
        }

        var ordered = TopologicalSorter.Sort(
            programmables.Select(c => c.Identity).ToList(),
            identity => dependsOn.TryGetValue(identity, out var list) ? list : []);

        var byIdentity = programmables.ToDictionary(c => c.Identity);
        var sequence = ordered.Ordered.Concat(ordered.Cyclic).Select(i => byIdentity[i]).ToList();

        foreach (var change in sequence)
        {
            var definition = change.Identity.Type switch
            {
                ObjectType.View => source.Views
                    .FirstOrDefault(v => v.Identity == change.Identity)?.Definition,
                ObjectType.Routine => source.Routines
                    .FirstOrDefault(r => r.Identity == change.Identity)?.Definition,
                ObjectType.Trigger => source.Triggers
                    .FirstOrDefault(t => t.Identity == change.Identity)?.Definition,
                _ => null
            };

            if (definition is null)
            {
                continue;
            }

            var (body, note) = WithCatalogName(definition, change.Identity);
            var what = $"{change.Identity.Type.ToString().ToLowerInvariant()} {change.Identity.QualifiedName}";

            steps.Add(new ScriptStep(
                ScriptPhase.Programmables,
                note is null ? what : $"{what} — {note}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.CreateOrAlter(body))));
        }
    }

    // sp_rename updates sys.objects.name and leaves sys.sql_modules.definition alone, so a renamed object's
    // stored text says CREATE ... <old name> for the rest of its life. Emitting that verbatim creates the
    // old name on the target: the apply reports success, the target holds an object nobody asked for, and
    // no later compare can converge. The name is taken from the catalog the comparison was built from
    // instead — which is also the only thing that fixes a header with no schema on it, where what the
    // CREATE lands on depends on the default schema of whoever runs the script.
    //
    // Only the header is rewritten. A body can name itself elsewhere — a scripted comment header, a
    // RAISERROR, a recursive EXEC — and those are deliberately left alone: the aim is a target object
    // identical to the source object, and the source carries exactly the same stale text. A recursive call
    // to the old name is already broken on the source, since nothing answers to that name there either.
    internal static (string Body, string? Note) WithCatalogName(string definition, ObjectIdentity identity)
    {
        var header = ProgrammableHeaderReader.Read(definition);

        if (header is null)
        {
            return (definition, "the CREATE in the stored body was not recognised, so it is emitted as stored");
        }

        if (header.Names(identity))
        {
            return (definition, null);
        }

        var note = header.Schema is null
            ? $"the stored body creates it unqualified as {header.Name}, so the schema is stated explicitly"
            : $"the stored body still creates it as {header.Schema}.{header.Name}, so the name comes from the catalog";

        return (header.ReplaceIn(definition, SqlServerQuoter.Instance.Qualify(identity)), note);
    }

    // Does any column name a type this script is creating? If so the statement cannot be compiled with
    // the rest of the batch, because the type does not exist yet at compile time.
    private static bool UsesNewType(TableDefinition table, HashSet<ObjectIdentity> newTypes) =>
        table.Columns.Any(c => c.DataType.IsUserDefined
            && newTypes.Contains(new ObjectIdentity(
                ObjectType.UserDefinedType, c.DataType.Schema ?? table.Identity.Schema, c.DataType.Name)));

    private static string Batch(string sql, bool defer) =>
        defer ? TSqlWriter.ExecuteAsBatch(sql) : sql;

    private static string RoutineKeyword(DatabaseSchema target, ObjectIdentity identity)
    {
        var routine = target.Routines.FirstOrDefault(r => r.Identity == identity);
        return routine?.Kind == RoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";
    }
}
