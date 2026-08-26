namespace DbDelta.Core.Model;

public sealed class ForeignKeyDefinition
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public required ObjectIdentity ReferencedTable { get; init; }

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public ReferentialAction OnDelete { get; init; } = ReferentialAction.NoAction;

    public ReferentialAction OnUpdate { get; init; } = ReferentialAction.NoAction;

    public bool IsDisabled { get; init; }
}
