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

        EmitSchemas(steps, changes);
        EmitDrops(steps, changes, target);
        EmitTypes(steps, changes, source, target);
        EmitSequences(steps, changes, source);
        EmitTableCreations(steps, changes, sourceTables, newTypes, target.Collation);
        EmitTableAlterations(steps, changes, sourceTables, targetTables, deferred, target.Collation);
        EmitProgrammables(steps, changes, source);

        return new SyncScript
        {
            Header = $"-- DbDelta · {source.DatabaseName} -> {target.DatabaseName}\n"
                + $"-- {changes.Count} object(s) changed",
            Steps = steps.OrderBy(s => s.Phase).ToList()
        };
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
        string? databaseCollation)
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
        HashSet<ObjectIdentity> deferred,
        string? databaseCollation)
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
                        EmitColumnChange(steps, child, table, source, target, rebuilt, defer, databaseCollation);
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
        bool defer,
        string? databaseCollation)
    {
        var name = child.Identity.Name;

        if (child.Kind == DiffKind.TargetOnly)
        {
            steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"drop column {table.QualifiedName}.{name}",
                TSqlWriter.DropColumn(table, name),
                Destructive: true));
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
                TSqlWriter.AddColumn(table, column, databaseCollation)));
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

        // A narrowing change is destructive even though it drops nothing: the server rounds DECIMAL scale
        // and DATETIME2 precision without complaint, so left unflagged it would rewrite existing rows and
        // report success. The verdict is type arithmetic over both sides, already known here.
        var existing = target.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        var narrowing = existing is null
            ? ColumnNarrowing.None
            : NarrowingAnalyzer.Analyse(existing, column);

        steps.Add(new ScriptStep(
            ScriptPhase.AlterColumns,
            narrowing.LosesData
                ? $"alter column {table.QualifiedName}.{name} — {narrowing.Reason}"
                : $"alter column {table.QualifiedName}.{name}",
            TSqlWriter.AlterColumn(table, column, databaseCollation),
            Destructive: narrowing.LosesData));
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
    private static (string Body, string? Note) WithCatalogName(string definition, ObjectIdentity identity)
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
