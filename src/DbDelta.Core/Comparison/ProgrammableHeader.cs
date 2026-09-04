using DbDelta.Core.Model;

namespace DbDelta.Core.Comparison;

// Where an object's own name sits inside its stored CREATE, and what that name says. The two can
// disagree with the catalog: sp_rename updates sys.objects.name and leaves sys.sql_modules.definition
// alone, so the stored text keeps saying CREATE ... <old name> for the life of the object.
public sealed record ProgrammableHeader(int Start, int Length, string? Schema, string Name)
{
    // An unqualified header does not name the object, even when the bare name matches: which schema
    // CREATE lands in depends on the default schema of whoever runs it, and that is not knowable from
    // here. So a missing schema counts as a disagreement and gets qualified rather than trusted.
    public bool Names(ObjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return Schema is not null
            && string.Equals(Schema, identity.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, identity.Name, StringComparison.OrdinalIgnoreCase);
    }

    public string ReplaceIn(string definition, string name)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return string.Concat(definition.AsSpan(0, Start), name, definition.AsSpan(Start + Length));
    }
}
