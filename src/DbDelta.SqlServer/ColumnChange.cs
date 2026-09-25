using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

// What one differing column needs, read off the properties the comparer reported. The point of splitting
// it up is that ALTER COLUMN is only one of four different statements a column difference can call for,
// and writing it for all of them is how the identity clause and a default nobody moved ended up in the
// same statement: ALTER COLUMN can change a type, a nullability or a collation, and nothing else.
internal sealed class ColumnChange
{
    private static readonly HashSet<string> Alterable = new(StringComparer.Ordinal) { "DataType", "Nullable", "Collation" };

    private ColumnChange(ObjectDiff diff, ColumnDefinition? source, ColumnDefinition? target)
    {
        Diff = diff;
        Source = source;
        Target = target;

        var properties = diff.Properties.Select(p => p.Property).ToHashSet(StringComparer.Ordinal);
        var bothComputed = source?.ComputedExpression is not null && target?.ComputedExpression is not null;
        var neitherComputed = source?.ComputedExpression is null && target?.ComputedExpression is null;

        IdentityDiffers = properties.Contains("Identity");
        DefaultDiffers = properties.Contains("Default");

        // A computed column holds no data of its own, so any difference in one is a drop and a re-add. A
        // column that is computed on one side only is a different matter: turning stored values into
        // derived ones, or the reverse, cannot be done to a column in place.
        RecreatesComputed = diff.Kind == DiffKind.Different && bothComputed && properties.Count > 0;
        ComputedKindDiffers = diff.Kind == DiffKind.Different && !bothComputed && !neitherComputed;

        NeedsAlter = diff.Kind == DiffKind.Different && neitherComputed && properties.Overlaps(Alterable);
        OrdinalOnly = diff.Kind == DiffKind.Different && properties.SetEquals(["Ordinal"]);
    }

    public ObjectDiff Diff { get; }

    public string Name => Diff.Identity.Name;

    public ColumnDefinition? Source { get; }

    public ColumnDefinition? Target { get; }

    public bool IsDrop => Diff.Kind == DiffKind.TargetOnly;

    public bool IsAdd => Diff.Kind == DiffKind.SourceOnly;

    public bool NeedsAlter { get; }

    public bool IdentityDiffers { get; }

    public bool DefaultDiffers { get; }

    public bool RecreatesComputed { get; }

    public bool ComputedKindDiffers { get; }

    public bool OrdinalOnly { get; }

    // SQL Server has no statement that adds identity to an existing column or takes it away, or that turns
    // a stored column into a computed one. A table rebuild is the only way to make either change.
    public bool NeedsRebuild => IdentityDiffers || ComputedKindDiffers;

    // Whether the column itself is altered, dropped or recreated — each of which the server refuses while
    // anything else still depends on the column.
    public bool TouchesColumn => NeedsAlter || IsDrop || RecreatesComputed;

    public static IReadOnlyList<ColumnChange> From(ObjectDiff table, TableDefinition source, TableDefinition target) =>
        table.DifferingChildren
            .Where(c => c.Identity.Type == ObjectType.Column)
            .Select(c => new ColumnChange(c, Find(source, c.Identity.Name), Find(target, c.Identity.Name)))
            .ToList();

    public string DescribeRebuildNeed()
    {
        if (IdentityDiffers)
        {
            return $"{Name} is {Describe(Source?.Identity)} on the source and {Describe(Target?.Identity)} on the target";
        }

        return Source?.ComputedExpression is not null
            ? $"{Name} is computed on the source and stored on the target"
            : $"{Name} is stored on the source and computed on the target";
    }

    private static string Describe(IdentitySpec? identity) =>
        identity is null ? "not an identity column" : $"IDENTITY({identity.Seed},{identity.Increment})";

    private static ColumnDefinition? Find(TableDefinition table, string name) =>
        table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
