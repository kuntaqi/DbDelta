using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

// Everything on the target that has to come down before a set of columns can be altered or dropped, found
// by walking what the server itself would refuse over. Each of these was an error 5074 first: an index,
// a filtered index whose WHERE names the column, a primary key, a unique constraint, a check constraint, a
// default, a computed column built from it, a hand-made statistics object, and a foreign key in either
// direction. Dropping a key has a second ring of its own — every foreign key that references it — which is
// why keys dropped for any reason are an input, not only the ones found here.
internal sealed class ColumnDependents
{
    public required IReadOnlyList<StatisticsDefinition> Statistics { get; init; }

    public required IReadOnlyList<IndexDefinition> Indexes { get; init; }

    public required IReadOnlyList<CheckConstraintDefinition> Checks { get; init; }

    public required IReadOnlyList<ColumnDefinition> Defaults { get; init; }

    public required IReadOnlyList<UniqueConstraintDefinition> Uniques { get; init; }

    public PrimaryKeyDefinition? PrimaryKey { get; init; }

    public required IReadOnlyList<ColumnDefinition> ComputedColumns { get; init; }

    public required IReadOnlyList<ForeignKeyDefinition> OutboundKeys { get; init; }

    public required IReadOnlyList<InboundForeignKey> InboundKeys { get; init; }

    public static ColumnDependents Find(
        DatabaseSchema database,
        TableDefinition table,
        IEnumerable<string> columns,
        IReadOnlyCollection<IReadOnlyList<string>> droppedKeys)
    {
        var touched = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);

        // A computed column is itself held by what depends on it, so it joins the set it was found from.
        // One pass is enough — a computed column cannot be built from another — but the loop costs nothing.
        var computed = new List<ColumnDefinition>();
        bool grew;
        do
        {
            grew = false;
            foreach (var column in table.Columns.Where(c => c.ComputedExpression is not null && !touched.Contains(c.Name)))
            {
                if (Reads(column.ComputedFrom, column.ComputedExpression, touched))
                {
                    computed.Add(column);
                    touched.Add(column.Name);
                    grew = true;
                }
            }
        }
        while (grew);

        // Columns being recreated as computed columns are dropped too, so they are in the list even though
        // nothing else put them there.
        computed.AddRange(table.Columns.Where(c =>
            c.ComputedExpression is not null && touched.Contains(c.Name) && !computed.Contains(c)));

        var indexes = table.Indexes
            .Where(i => i.Columns.Any(c => touched.Contains(c.Name))
                || i.IncludedColumns.Any(touched.Contains)
                || Names(i.FilterExpression, touched))
            .ToList();

        var uniques = table.UniqueConstraints.Where(u => u.Columns.Any(c => touched.Contains(c.Name))).ToList();
        var primaryKey = table.PrimaryKey is { } pk && pk.Columns.Any(c => touched.Contains(c.Name)) ? pk : null;

        var keys = new List<IReadOnlyList<string>>(droppedKeys);
        keys.AddRange(indexes.Where(i => i.IsUnique).Select(i => (IReadOnlyList<string>)i.Columns.Select(c => c.Name).ToList()));
        keys.AddRange(uniques.Select(u => (IReadOnlyList<string>)u.Columns.Select(c => c.Name).ToList()));

        if (primaryKey is not null)
        {
            keys.Add(primaryKey.Columns.Select(c => c.Name).ToList());
        }

        bool HoldsReference(ForeignKeyDefinition key) =>
            key.ReferencedColumns.Any(touched.Contains) || keys.Any(k => SameColumns(k, key.ReferencedColumns));

        return new ColumnDependents
        {
            Statistics = table.Statistics
                .Where(s => s.Columns.Any(touched.Contains) || Names(s.FilterExpression, touched))
                .ToList(),
            Indexes = indexes,
            Checks = table.CheckConstraints.Where(c => Reads(c.Columns, c.Expression, touched)).ToList(),
            Defaults = table.Columns
                .Where(c => c.DefaultConstraintName is not null && touched.Contains(c.Name))
                .ToList(),
            Uniques = uniques,
            PrimaryKey = primaryKey,
            ComputedColumns = computed,
            OutboundKeys = table.ForeignKeys
                .Where(f => f.Columns.Any(touched.Contains) || (f.ReferencedTable == table.Identity && HoldsReference(f)))
                .ToList(),
            InboundKeys = database.Tables
                .Where(t => t.Identity != table.Identity)
                .SelectMany(t => t.ForeignKeys
                    .Where(f => f.ReferencedTable == table.Identity && HoldsReference(f))
                    .Select(f => new InboundForeignKey(t.Identity, f)))
                .ToList()
        };
    }

    // The catalog's own list when there is one. Without it — the dependency query was refused — the stored
    // definition is searched instead, which is reliable for the one reason that the server writes every
    // column reference in a stored expression bracketed: ([A]<[B]).
    private static bool Reads(IReadOnlyList<string> columns, string? expression, HashSet<string> touched) =>
        columns.Count > 0 ? columns.Any(touched.Contains) : Names(expression, touched);

    private static bool Names(string? expression, HashSet<string> columns) =>
        expression is not null
        && columns.Any(c => expression.Contains($"[{c.Replace("]", "]]", StringComparison.Ordinal)}]", StringComparison.OrdinalIgnoreCase));

    private static bool SameColumns(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.All(l => right.Contains(l, StringComparer.OrdinalIgnoreCase));
}
