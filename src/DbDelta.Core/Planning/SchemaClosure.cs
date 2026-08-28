using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

// A tick states intent, not sufficiency. Creating dbo.Company means creating dbo.Category too when the
// target does not have it — otherwise the foreign key at the end of the script fails and takes the whole
// transaction with it. Closure walks what the picked objects reference and adds what the target lacks.
//
// Only objects absent from the target are pulled in. One that exists but differs is left alone: the
// reference resolves against what is already there, and dragging every differing neighbour into the plan
// would grow it far past what was asked for. The exception is a parent missing the exact columns a
// foreign key points at, which is the same failure wearing a different shape.
//
// Closure is computed from the selection rather than stored in it, so unticking an object also removes
// whatever it alone required. Writing prerequisites into the selection would leave them behind.
public static class SchemaClosure
{
    public static ClosureResult Expand(
        DatabaseSchema source,
        DatabaseSchema target,
        SchemaDiff diff,
        IEnumerable<ObjectIdentity> selected,
        IReadOnlySet<ObjectIdentity>? blocked = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selected);

        var sourceTables = source.Tables.ToDictionary(t => t.Identity);
        var targetTables = target.Tables.ToDictionary(t => t.Identity);
        var onTarget = OnTarget(target);

        // Stable input order, so the same picks always report the same additions in the same sequence.
        var seeds = selected
            .Distinct()
            .OrderBy(i => i.Type)
            .ThenBy(i => i.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selection = new HashSet<ObjectIdentity>(seeds);
        var queue = new Queue<ObjectIdentity>(seeds);
        var required = new List<RequiredObject>();
        var refused = new List<RequiredObject>();
        var unsatisfiable = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var identity = queue.Dequeue();

            foreach (var (needed, reason) in Prerequisites(identity, source, diff, sourceTables, targetTables, onTarget))
            {
                if (selection.Contains(needed))
                {
                    continue;
                }

                // An exclusion is a decision, and closure is not entitled to overrule one. The
                // prerequisite is named and the walk stops there rather than quietly reinstating it.
                if (blocked is not null && blocked.Contains(needed))
                {
                    if (!refused.Any(r => r.Identity == needed && r.RequiredBy == identity))
                    {
                        refused.Add(new RequiredObject(needed, identity, reason));
                    }

                    continue;
                }

                var change = diff.Find(needed);
                if (change is null || !change.HasChanges)
                {
                    var message =
                        $"{identity.QualifiedName} needs {needed.QualifiedName}, which the target does not have "
                        + "and this comparison cannot create.";

                    if (reported.Add(message))
                    {
                        unsatisfiable.Add(message);
                    }

                    continue;
                }

                selection.Add(needed);
                required.Add(new RequiredObject(needed, identity, reason));
                queue.Enqueue(needed);
            }
        }

        return new ClosureResult
        {
            Selection = selection,
            Required = required,
            Blocked = refused,
            Unsatisfiable = unsatisfiable
        };
    }

    private static IEnumerable<(ObjectIdentity Needed, string Reason)> Prerequisites(
        ObjectIdentity identity,
        DatabaseSchema source,
        SchemaDiff diff,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables,
        Dictionary<ObjectIdentity, TableDefinition> targetTables,
        HashSet<ObjectIdentity> onTarget) =>
        identity.Type switch
        {
            ObjectType.Table => TablePrerequisites(identity, diff, sourceTables, targetTables, onTarget),
            ObjectType.View => BodyPrerequisites(
                identity,
                source.Views.FirstOrDefault(v => v.Identity == identity)?.DependsOn,
                onTarget),
            ObjectType.Routine => BodyPrerequisites(
                identity,
                source.Routines.FirstOrDefault(r => r.Identity == identity)?.DependsOn,
                onTarget),
            ObjectType.Trigger => TriggerPrerequisites(identity, source, onTarget),
            _ => []
        };

    private static IEnumerable<(ObjectIdentity, string)> TablePrerequisites(
        ObjectIdentity identity,
        SchemaDiff diff,
        Dictionary<ObjectIdentity, TableDefinition> sourceTables,
        Dictionary<ObjectIdentity, TableDefinition> targetTables,
        HashSet<ObjectIdentity> onTarget)
    {
        var change = diff.Find(identity);
        if (change is null || !sourceTables.TryGetValue(identity, out var table))
        {
            yield break;
        }

        // A column can name a type as well as a table. An alias type is not a built-in, so a column using
        // one cannot be written before the type exists — the same shape of prerequisite as a foreign key,
        // one level down.
        var columns = change.Kind == DiffKind.SourceOnly
            ? table.Columns
            : table.Columns.Where(c => IsColumnBeingAdded(change, c.Name)).ToList();

        foreach (var column in columns.Where(c => c.DataType.IsUserDefined))
        {
            var type = new ObjectIdentity(
                ObjectType.UserDefinedType, column.DataType.Schema ?? identity.Schema, column.DataType.Name);

            if (!onTarget.Contains(type))
            {
                yield return (type, $"{identity.Name}.{column.Name} is of that type");
            }
        }

        // A created table brings every one of its keys; an altered one brings only the keys being added,
        // since the rest were satisfied when the target was built.
        var keys = change.Kind == DiffKind.SourceOnly
            ? table.ForeignKeys
            : table.ForeignKeys.Where(fk => IsBeingAdded(change, fk.Name)).ToList();

        foreach (var fk in keys)
        {
            if (fk.ReferencedTable == identity)
            {
                continue;
            }

            if (!onTarget.Contains(fk.ReferencedTable))
            {
                yield return (fk.ReferencedTable, $"{fk.Name} references it and the target does not have it");
                continue;
            }

            // The table is there but the columns the key points at are not, so the ALTER that adds them
            // has to be in the plan as well. Same failure as a missing table, one level down.
            if (!targetTables.TryGetValue(fk.ReferencedTable, out var parent))
            {
                continue;
            }

            var missing = fk.ReferencedColumns
                .Where(c => !parent.Columns.Any(pc => string.Equals(pc.Name, c, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (missing.Count > 0)
            {
                yield return (
                    fk.ReferencedTable,
                    $"{fk.Name} references {fk.ReferencedTable.QualifiedName} ({string.Join(", ", missing)}), "
                    + "which the copy on the target does not have");
            }
        }
    }

    // Views and routines depend through their SQL bodies. CREATE OR ALTER compiles the body either way,
    // so an altered one needs its references present just as much as a new one does.
    private static IEnumerable<(ObjectIdentity, string)> BodyPrerequisites(
        ObjectIdentity identity,
        IReadOnlyList<ObjectIdentity>? dependsOn,
        HashSet<ObjectIdentity> onTarget)
    {
        foreach (var dependency in dependsOn ?? [])
        {
            if (dependency == identity || onTarget.Contains(dependency))
            {
                continue;
            }

            // A type is not read from, it is named — as a parameter, or in a declaration inside the body.
            // Either way it has to exist when the body compiles, which is the same prerequisite; only the
            // sentence explaining it to someone reading the plan is different.
            yield return dependency.Type == ObjectType.UserDefinedType
                ? (dependency, $"{identity.QualifiedName} names that type")
                : (dependency, $"{identity.QualifiedName} reads from it");
        }
    }

    private static IEnumerable<(ObjectIdentity, string)> TriggerPrerequisites(
        ObjectIdentity identity,
        DatabaseSchema source,
        HashSet<ObjectIdentity> onTarget)
    {
        var trigger = source.Triggers.FirstOrDefault(t => t.Identity == identity);

        if (trigger is not null && !onTarget.Contains(trigger.Table))
        {
            yield return (trigger.Table, $"{identity.QualifiedName} is a trigger on it");
        }
    }

    private static bool IsColumnBeingAdded(ObjectDiff change, string name) =>
        change.DifferingChildren.Any(c =>
            c.Identity.Type == ObjectType.Column
            && c.Kind == DiffKind.SourceOnly
            && string.Equals(c.Identity.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsBeingAdded(ObjectDiff change, string name) =>
        change.DifferingChildren.Any(c =>
            c.Identity.Type == ObjectType.ForeignKey
            && c.Kind == DiffKind.SourceOnly
            && string.Equals(c.Identity.Name, name, StringComparison.OrdinalIgnoreCase));

    private static HashSet<ObjectIdentity> OnTarget(DatabaseSchema target)
    {
        var present = new HashSet<ObjectIdentity>();

        foreach (var identity in target.Tables.Select(t => t.Identity)
            .Concat(target.Views.Select(v => v.Identity))
            .Concat(target.Routines.Select(r => r.Identity))
            .Concat(target.Triggers.Select(t => t.Identity))
            .Concat(target.Sequences.Select(s => s.Identity))
            .Concat(target.UserDefinedTypes.Select(t => t.Identity)))
        {
            present.Add(identity);
        }

        return present;
    }
}
