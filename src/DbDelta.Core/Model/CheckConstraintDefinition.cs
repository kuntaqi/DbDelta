namespace DbDelta.Core.Model;

public sealed class CheckConstraintDefinition
{
    public required string Name { get; init; }

    public required string Expression { get; init; }

    public bool IsDisabled { get; init; }
}
