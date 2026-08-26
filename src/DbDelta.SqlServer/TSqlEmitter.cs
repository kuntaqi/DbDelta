using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
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

        EmitSchemas(steps, changes, sourceTables);
        EmitDrops(steps, changes, target);
        EmitTableCreations(steps, changes, sourceTables);
        EmitTableAlterations(steps, changes, sourceTables, targetTables, deferred);
        EmitProgrammables(steps, changes, source);

        return new SyncScript
        {
            Header = $"-- DbDelta · {source.DatabaseName} -> {target.DatabaseName}\n"
                + $"-- {changes.Count} object(s) changed",
            Steps = steps.OrderBy(s => s.Phase).ToList()
        };
    }

    private static void EmitSchemas(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables)
    {
        var schemas = changes
            .Where(c => c.Kind == DiffKind.SourceOnly)
            .Select(c => c.Identity.Schema)
            .Concat(sourceTables.Keys.Select(k => k.Schema))
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
                        TSqlWriter.DropTable(change.Identity)));
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
        Dictionary<ObjectIdentity, TableDefinition> sourceTables)
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
                TSqlWriter.CreateTable(table)));

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
                steps.Add(new ScriptStep(
                    ScriptPhase.Indexes,
                    $"index {index.Name}",
                    TSqlWriter.CreateIndex(table.Identity, index)));
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
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables,
        Dictionary<ObjectIdentity, TableDefinition> targetTables,
        HashSet<ObjectIdentity> deferred)
    {
        foreach (var change in changes.Where(c => c.Kind == DiffKind.Different && c.Identity.Type == ObjectType.Table))
        {
            if (!sourceTables.TryGetValue(change.Identity, out var source)
                || !targetTables.TryGetValue(change.Identity, out var target))
            {
                continue;
            }

            var table = change.Identity;
            var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defer = deferred.Contains(table);

            foreach (var child in change.DifferingChildren)
            {
                switch (child.Identity.Type)
                {
                    case ObjectType.Column:
                        EmitColumnChange(steps, child, table, source, target, rebuilt, defer);
                        break;

                    case ObjectType.Index:
                        EmitIndexChange(steps, child, table, source, rebuilt, defer);
                        break;

                    case ObjectType.CheckConstraint:
                        EmitCheckChange(steps, child, table, source, defer);
                        break;

                    case ObjectType.ForeignKey:
                        EmitForeignKeyChange(steps, child, table, source, defer);
                        break;

                    case ObjectType.PrimaryKey:
                    case ObjectType.UniqueConstraint:
                        EmitKeyChange(steps, child, table, source, defer);
                        break;

                    default:
                        break;
                }
            }
        }
    }

    private static void EmitColumnChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        TableDefinition target,
        HashSet<string> rebuilt,
        bool defer)
    {
        var name = child.Identity.Name;

        if (child.Kind == DiffKind.TargetOnly)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"drop column {table.QualifiedName}.{name}",
                TSqlWriter.DropColumn(table, name)));
            return;
        }

        var column = source.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            return;
        }

        if (child.Kind == DiffKind.SourceOnly)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"add column {table.QualifiedName}.{name}",
                TSqlWriter.AddColumn(table, column)));
            return;
        }

        // SQL Server refuses to alter a column an index depends on, so dependent indexes come down
        // first and go back up in the index phase. Discovered against a filtered unique index.
        foreach (var dependent in DependentIndexes(target, name))
        {
            if (!rebuilt.Add(dependent.Name))
            {
                continue;
            }

            steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"drop index {dependent.Name} — depends on {name}",
                TSqlWriter.DropIndex(table, dependent.Name)));

            var replacement = source.Indexes
                .FirstOrDefault(i => string.Equals(i.Name, dependent.Name, StringComparison.OrdinalIgnoreCase));

            if (replacement is not null)
            {
                steps.Add(new ScriptStep(
                    ScriptPhase.Indexes,
                    $"recreate index {replacement.Name}",
                    Batch(TSqlWriter.CreateIndex(table, replacement), defer)));
            }
        }

        steps.Add(new ScriptStep(
            ScriptPhase.AlterColumns,
            $"alter column {table.QualifiedName}.{name}",
            TSqlWriter.AlterColumn(table, column)));
    }

    private static void EmitIndexChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        HashSet<string> rebuilt,
        bool defer)
    {
        var name = child.Identity.Name;

        if (!rebuilt.Add(name))
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
                steps.Add(new ScriptStep(
                    ScriptPhase.Indexes,
                    $"index {name}",
                    Batch(TSqlWriter.CreateIndex(table, index), defer)));
            }
        }
    }

    private static void EmitCheckChange(
        List<ScriptStep> steps,
        ObjectDiff child,
        ObjectIdentity table,
        TableDefinition source,
        bool defer)
    {
        var name = child.Identity.Name;

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
        bool defer)
    {
        var name = child.Identity.Name;

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
        bool defer)
    {
        var name = child.Identity.Name;

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

    private static void EmitProgrammables(
        List<ScriptStep> steps,
        List<ObjectDiff> changes,
        DatabaseSchema source)
    {
        foreach (var change in changes.Where(c => c.Kind is DiffKind.SourceOnly or DiffKind.Different))
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

            steps.Add(new ScriptStep(
                ScriptPhase.Programmables,
                $"{change.Identity.Type.ToString().ToLowerInvariant()} {change.Identity.QualifiedName}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.CreateOrAlter(definition))));
        }
    }

    private static string Batch(string sql, bool defer) =>
        defer ? TSqlWriter.ExecuteAsBatch(sql) : sql;

    private static IEnumerable<IndexDefinition> DependentIndexes(TableDefinition table, string column) =>
        table.Indexes.Where(i =>
            i.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
            || i.IncludedColumns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)));

    private static string RoutineKeyword(DatabaseSchema target, ObjectIdentity identity)
    {
        var routine = target.Routines.FirstOrDefault(r => r.Identity == identity);
        return routine?.Kind == RoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";
    }
}
