namespace DbDelta.Core.Model;

public sealed class ViewDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required string Definition { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    // Other programmable objects this one references. Views and routines depend through their SQL
    // bodies rather than through constraints, so emission order cannot be derived from foreign keys.
    public IReadOnlyList<ObjectIdentity> DependsOn { get; init; } = [];

    public override string ToString() => Identity.QualifiedName;
}
