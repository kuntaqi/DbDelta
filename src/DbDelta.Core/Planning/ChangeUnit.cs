namespace DbDelta.Core.Planning;

public sealed class ChangeUnit
{
    public required ChangeUnitId Id { get; init; }

    public required string Description { get; init; }

    public override string ToString() => Description;
}
