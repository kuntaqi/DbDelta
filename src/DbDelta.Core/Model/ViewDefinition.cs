namespace DbDelta.Core.Model;

public sealed class ViewDefinition
{
    public required ObjectIdentity Identity { get; init; }

    public required string Definition { get; init; }

    public ProviderExtras Extras { get; init; } = ProviderExtras.Empty;

    // The tables, views and routines this one references. Views and routines depend through their SQL
    // bodies rather than through constraints, so neither emission order nor closure can be derived from
    // foreign keys.
    public IReadOnlyList<ObjectIdentity> DependsOn { get; init; } = [];

    public override string ToString() => Identity.QualifiedName;
}
