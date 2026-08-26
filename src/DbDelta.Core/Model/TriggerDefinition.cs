namespace DbDelta.Core.Model;

public sealed class TriggerDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required ObjectIdentity Table { get; init; }

    public required string Definition { get; init; }

    public bool IsDisabled { get; init; }
}
