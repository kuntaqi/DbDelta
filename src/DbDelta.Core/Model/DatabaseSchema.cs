namespace DbDelta.Core.Model;

public sealed class DatabaseSchema
{
    public required string DatabaseName { get; init; }

    public string? Collation { get; init; }

    public IReadOnlyList<TableDefinition> Tables { get; init; } = [];

    public IReadOnlyList<ViewDefinition> Views { get; init; } = [];

    public IReadOnlyList<RoutineDefinition> Routines { get; init; } = [];

    public IReadOnlyList<TriggerDefinition> Triggers { get; init; } = [];

    public IReadOnlyList<SequenceDefinition> Sequences { get; init; } = [];

    public bool IsEmpty =>
        Tables.Count == 0
        && Views.Count == 0
        && Routines.Count == 0
        && Triggers.Count == 0
        && Sequences.Count == 0;

    public int ObjectCount =>
        Tables.Count + Views.Count + Routines.Count + Triggers.Count + Sequences.Count;
}
