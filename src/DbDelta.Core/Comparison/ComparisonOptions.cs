namespace DbDelta.Core.Comparison;

public sealed class ComparisonOptions
{
    public static readonly ComparisonOptions Default = new();

    // Formatting-only edits to a view or procedure body are noise, not a change worth deploying.
    public bool IgnoreWhitespaceInBodies { get; init; } = true;

    public bool IgnoreColumnOrder { get; init; } = true;

    public bool IgnoreCollation { get; init; }

    public bool IgnoreFillFactor { get; init; } = true;

    public IReadOnlySet<string> IgnoredSchemas { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool ShouldCompareSchema(string schema) => !IgnoredSchemas.Contains(schema);
}
