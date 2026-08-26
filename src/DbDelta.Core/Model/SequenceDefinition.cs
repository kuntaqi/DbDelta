namespace DbDelta.Core.Model;

public sealed class SequenceDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required DataTypeSpec DataType { get; init; }

    public long StartValue { get; init; }

    public long Increment { get; init; } = 1;

    public long? MinValue { get; init; }

    public long? MaxValue { get; init; }

    public bool IsCycling { get; init; }
}
