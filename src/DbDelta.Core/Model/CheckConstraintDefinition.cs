namespace DbDelta.Core.Model;

public sealed class CheckConstraintDefinition
{
    public required string Name { get; init; }

    public required string Expression { get; init; }

    public bool IsDisabled { get; init; }

    // The columns the expression reads, from the catalog rather than from parsing it. Not compared: it is
    // derived from Expression, which is.
    public IReadOnlyList<string> Columns { get; init; } = [];
}
