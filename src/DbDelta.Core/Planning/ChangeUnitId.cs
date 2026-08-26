using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

// The dedup key. Two overlapping selections that produce the same change land on the same id, which
// is what makes duplicate statements structurally impossible rather than merely unlikely.
public sealed record ChangeUnitId(ObjectIdentity Object, ChangeUnitKind Kind, string? RowKey = null)
{
    public bool IsRowChange => RowKey is not null;

    public override string ToString() =>
        RowKey is null ? $"{Kind} {Object.QualifiedName}" : $"{Kind} {Object.QualifiedName}[{RowKey}]";
}
