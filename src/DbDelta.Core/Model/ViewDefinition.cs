namespace DbDelta.Core.Model;

public sealed class ViewDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required string Definition { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    public override string ToString() => Identity.QualifiedName;
}
