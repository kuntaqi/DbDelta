namespace DbDelta.Core.Planning;

public sealed class TopologicalSortResult<T>
    where T : notnull
{
    public required IReadOnlyList<T> Ordered { get; init; }

    public required IReadOnlyList<T> Cyclic { get; init; }

    public bool HasCycles => Cyclic.Count > 0;
}
