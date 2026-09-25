using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer;

// A table rebuilt from the source definition, for the differences no ALTER can make: identity added or
// removed, or a column turned from stored to computed. The copy is built beside the original under a
// temporary name, filled, and swapped in, all inside the script's one transaction:
//
//   1. inbound foreign keys come down, since the original cannot be dropped while anything points at it;
//   2. the copy is created from the source columns, with no constraints yet — default, key and check names
//      are unique per schema, and the original still holds them;
//   3. every row is copied, under IDENTITY_INSERT when the copy has identity, and the counts are asserted;
//   4. the original is dropped and the copy renamed onto it, with the original's grants re-issued;
//   5. defaults, keys, indexes, checks and foreign keys go back on from the source, the target's own
//      statistics and triggers from the target, and the views that read it are refreshed.
//
// Each statement runs as its own batch: the table a name resolves to changes halfway through, and a
// statement compiled up front would keep the object that was there when the batch began.
internal static class TableRebuild
{
    private const string Suffix = "_dbdelta_rebuild";

    private static readonly SqlServerQuoter Q = SqlServerQuoter.Instance;

    private static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase) { "timestamp", "rowversion" };

    // Why this table cannot be rebuilt, or null. The rebuilt table would be an ordinary one, so anything
    // the original is beyond that — versioned, memory-optimized, partitioned, published — would be lost,
    // and a schema-bound module would stop the DROP outright.
    public static string? Blocker(TableDefinition target)
    {
        var reasons = new List<string>();

        if (target.Extras[TableExtras.RebuildBlockers] is { } storage)
        {
            reasons.Add(storage);
        }

        if (target.SchemaBoundReferences.Count > 0)
        {
            var modules = target.SchemaBoundReferences.Select(r => r.Module.QualifiedName).Order().ToList();
            reasons.Add($"{string.Join(", ", modules)} {(modules.Count == 1 ? "is" : "are")} schema-bound to it");
        }

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    public static void Emit(
        EmitContext context,
        TableDefinition source,
        TableDefinition target,
        IReadOnlyList<ColumnChange> columns)
    {
        var table = source.Identity;
        var copy = new ObjectIdentity(ObjectType.Table, table.Schema, CopyName(context.Target, table));
        var why = string.Join("; ", columns.Where(c => c.NeedsRebuild).Select(c => c.DescribeRebuildNeed()));

        foreach (var inbound in context.Target.Tables
            .Where(t => t.Identity != table)
            .SelectMany(t => t.ForeignKeys
                .Where(f => f.ReferencedTable == table)
                .Select(f => new InboundForeignKey(t.Identity, f))))
        {
            context.BracketInbound(inbound, $"{table.QualifiedName} is rebuilt");
        }

        context.Steps.Add(new ScriptStep(
            ScriptPhase.AlterColumns,
            $"rebuild {table.QualifiedName}: create the copy {copy.Name} — {why}, which no ALTER can change",
            TSqlWriter.ExecuteAsBatch(TSqlWriter.CreateTable(copy, source, includeDefaults: false, context.Collation))));

        EmitCopy(context, source, target, table, copy, columns);
        EmitSwap(context, table, copy);
        EmitConstraints(context, source, target, table);
        EmitTargetOwned(context, target, table);
    }

    private static void EmitCopy(
        EmitContext context,
        TableDefinition source,
        TableDefinition target,
        ObjectIdentity table,
        ObjectIdentity copy,
        IReadOnlyList<ColumnChange> columns)
    {
        var into = new List<string>();
        var from = new List<string>();

        foreach (var column in source.Columns.OrderBy(c => c.OrdinalPosition))
        {
            if (column.ComputedExpression is not null || Generated.Contains(column.DataType.Name))
            {
                continue;
            }

            var existing = target.Columns.FirstOrDefault(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));

            // A column the target does not have yet gets its default, written into the SELECT because the
            // copy has no default constraints yet. Without a default it is left out and gets NULL — or
            // fails on NOT NULL, exactly as ADD COLUMN would.
            if (existing is not null)
            {
                into.Add(Q.Quote(column.Name));
                from.Add(Q.Quote(existing.Name));
            }
            else if (column.DefaultExpression is not null && column.Identity is null)
            {
                into.Add(Q.Quote(column.Name));
                from.Add(column.DefaultExpression);
            }
        }

        var identity = source.Columns.FirstOrDefault(c =>
            c.Identity is not null && into.Contains(Q.Quote(c.Name), StringComparer.OrdinalIgnoreCase));

        var sql = new List<string>();

        if (identity is not null)
        {
            sql.Add($"SET IDENTITY_INSERT {Q.Qualify(copy)} ON;");
        }

        if (into.Count > 0)
        {
            sql.Add($"INSERT INTO {Q.Qualify(copy)} ({string.Join(", ", into)})\n"
                + $"SELECT {string.Join(", ", from)}\nFROM {Q.Qualify(table)};");
        }

        if (identity is not null)
        {
            sql.Add($"SET IDENTITY_INSERT {Q.Qualify(copy)} OFF;");

            // IDENTITY_INSERT leaves the copy's counter at the highest value inserted, which is behind the
            // original's wherever the top rows were deleted. Carried forward, so no key is issued twice.
            var old = target.Columns.FirstOrDefault(c => string.Equals(c.Name, identity.Name, StringComparison.OrdinalIgnoreCase));
            if (old?.Identity is { Increment: > 0 } && identity.Identity!.Increment > 0)
            {
                sql.Add($"DECLARE @current numeric(38, 0) = IDENT_CURRENT({Literal(table)});\n"
                    + $"IF @current > IDENT_CURRENT({Literal(copy)})\n"
                    + $"    DBCC CHECKIDENT ({Literal(copy)}, RESEED, @current) WITH NO_INFOMSGS;");
            }
        }

        sql.Add($"IF (SELECT COUNT_BIG(*) FROM {Q.Qualify(copy)}) <> (SELECT COUNT_BIG(*) FROM {Q.Qualify(table)})\n"
            + $"    THROW 50000, N'The rebuilt copy of {Escape(table.QualifiedName)} does not hold the same number of rows as the original.', 1;");

        var lost = target.Columns
            .Where(t => !source.Columns.Any(s => string.Equals(s.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t.ComputedExpression is null)
            .Select(t => t.Name)
            .ToList();

        var narrowed = columns
            .Where(c => c.Source is not null && c.Target is not null && c.Source.ComputedExpression is null)
            .Select(c => NarrowingAnalyzer.Analyse(c.Target!, c.Source!))
            .Where(n => n.LosesData)
            .Select(n => (n.Reason ?? string.Empty).TrimEnd('.'))
            .ToList();

        var notes = new List<string>();

        if (lost.Count > 0)
        {
            notes.Add($"{string.Join(", ", lost)} {(lost.Count == 1 ? "is" : "are")} not on the source and {(lost.Count == 1 ? "is" : "are")} not copied");
        }

        notes.AddRange(narrowed);

        context.Steps.Add(new ScriptStep(
            ScriptPhase.AlterColumns,
            notes.Count == 0
                ? $"rebuild {table.QualifiedName}: copy every row into {copy.Name}"
                : $"rebuild {table.QualifiedName}: copy every row into {copy.Name} — {string.Join("; ", notes)}",
            TSqlWriter.ExecuteAsBatch(string.Join("\n", sql)),
            Destructive: notes.Count > 0));
    }

    // Grants are the one thing attached to the table object itself that DbDelta does not read, so they
    // are re-issued from the catalog as the script runs rather than from anything read at compare time.
    // A column grant survives only if the rebuilt table still has that column.
    private static void EmitSwap(EmitContext context, ObjectIdentity table, ObjectIdentity copy)
    {
        var name = Q.Qualify(table);

        var sql = $"""
            DECLARE @grants nvarchar(max) = N'';
            SELECT @grants = @grants
                + CASE p.state WHEN 'D' THEN N'DENY ' ELSE N'GRANT ' END + p.permission_name COLLATE DATABASE_DEFAULT
                + N' ON {Escape(name)}' + ISNULL(N' (' + QUOTENAME(c.name) + N')', N'')
                + N' TO ' + QUOTENAME(pr.name)
                + CASE p.state WHEN 'W' THEN N' WITH GRANT OPTION' ELSE N'' END + N'; '
            FROM sys.database_permissions p
            JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id
            LEFT JOIN sys.columns c ON c.object_id = p.major_id AND c.column_id = p.minor_id AND p.minor_id > 0
            WHERE p.class = 1 AND p.major_id = OBJECT_ID({Literal(table)})
              AND (p.minor_id = 0 OR COL_LENGTH({Literal(copy)}, c.name) IS NOT NULL);
            DROP TABLE {name};
            EXEC sp_rename {Literal(copy)}, N'{Escape(table.Name)}';
            IF @grants <> N'' EXEC (@grants);
            """;

        context.Steps.Add(new ScriptStep(
            ScriptPhase.AlterColumns,
            $"rebuild {table.QualifiedName}: drop the original and rename {copy.Name} onto it, keeping its grants",
            TSqlWriter.ExecuteAsBatch(sql),
            Destructive: true));
    }

    private static void EmitConstraints(
        EmitContext context,
        TableDefinition source,
        TableDefinition target,
        ObjectIdentity table)
    {
        foreach (var column in source.Columns.Where(c => c.DefaultExpression is not null && c.DefaultConstraintName is not null))
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.AlterColumns,
                $"rebuild {table.QualifiedName}: default {column.DefaultConstraintName}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.AddDefault(table, column))));
        }

        if (source.PrimaryKey is not null)
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.Keys,
                $"primary key {source.PrimaryKey.Name}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.AddPrimaryKey(table, source.PrimaryKey))));
        }

        foreach (var unique in source.UniqueConstraints)
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.Keys,
                $"unique constraint {unique.Name}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.AddUniqueConstraint(table, unique))));
        }

        foreach (var index in source.Indexes)
        {
            TSqlEmitter.AddIndexStep(context.Steps, context.Refusals, table, index, $"index {index.Name}", defer: true);
        }

        // Statistics are not compared, so the rebuilt table gets the target's own back — where every
        // column they cover survived the rebuild.
        foreach (var statistics in target.Statistics.Where(s => s.Columns.All(c =>
            source.Columns.Any(sc => string.Equals(sc.Name, c, StringComparison.OrdinalIgnoreCase)))))
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.Indexes,
                $"restore statistics {statistics.Name} on {table.QualifiedName}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.CreateStatistics(table, statistics))));
        }

        foreach (var check in source.CheckConstraints)
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.CheckConstraints,
                $"check {check.Name}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.AddCheckConstraint(table, check))));
        }

        foreach (var key in source.ForeignKeys)
        {
            context.Steps.Add(new ScriptStep(
                ScriptPhase.AddForeignKeys,
                $"foreign key {key.Name}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.AddForeignKey(table, key))));
        }
    }

    // Triggers are objects of their own in the plan, and they went down with the original table. One the
    // plan creates or alters comes back through the plan; one it drops stays dropped; every other one is
    // put back as the target had it, disabled if it was disabled.
    private static void EmitTargetOwned(EmitContext context, TableDefinition target, ObjectIdentity table)
    {
        foreach (var trigger in context.Target.Triggers.Where(t => t.Table == table && !context.InPlan(t.Identity)))
        {
            var (body, _) = TSqlEmitter.WithCatalogName(trigger.Definition, trigger.Identity);

            context.Trailing.Add(new ScriptStep(
                ScriptPhase.Programmables,
                $"restore trigger {trigger.Identity.QualifiedName} on {table.QualifiedName}",
                TSqlWriter.ExecuteAsBatch(TSqlWriter.CreateOrAlter(body))));

            if (trigger.IsDisabled)
            {
                context.Trailing.Add(new ScriptStep(
                    ScriptPhase.Programmables,
                    $"disable trigger {trigger.Identity.QualifiedName}",
                    TSqlWriter.DisableTrigger(trigger.Identity, table)));
            }
        }

        var readers = context.Target.Views
            .Where(v => v.DependsOn.Contains(table))
            .Select(v => v.Identity)
            .Concat(context.Target.Routines
                .Where(r => r.Kind != RoutineKind.Procedure && r.DependsOn.Contains(table))
                .Select(r => r.Identity))
            .Where(m => !context.InPlan(m))
            .Where(m => !target.SchemaBoundReferences.Any(r => r.Module == m));

        foreach (var module in readers)
        {
            context.Trailing.Add(new ScriptStep(
                ScriptPhase.Programmables,
                $"refresh {module.QualifiedName}, which reads the rebuilt {table.QualifiedName}",
                TSqlWriter.RefreshModule(module)));
        }
    }

    private static string CopyName(DatabaseSchema target, ObjectIdentity table)
    {
        // Room for the suffix and a two-digit counter inside the 128 characters a name can have.
        var stem = table.Name.Length > 128 - Suffix.Length - 2 ? table.Name[..(128 - Suffix.Length - 2)] : table.Name;
        var name = stem + Suffix;

        for (var n = 2; target.Tables.Any(t => t.Identity == new ObjectIdentity(ObjectType.Table, table.Schema, name)); n++)
        {
            name = $"{stem}{Suffix}{n}";
        }

        return name;
    }

    private static string Literal(ObjectIdentity identity) => $"N'{Escape(Q.Qualify(identity))}'";

    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
