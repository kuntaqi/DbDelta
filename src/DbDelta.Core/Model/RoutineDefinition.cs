namespace DbDelta.Core.Model;

public sealed class RoutineDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required RoutineKind Kind { get; init; }

    public required string Definition { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => $"{Kind} {Identity.QualifiedName}";
}
