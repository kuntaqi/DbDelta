using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

// Turns a schema diff into the change units the cart is made of. One differing object is one unit: an
// object is either created, altered or dropped, never two of those at once.
//
// The kind is part of the identity rather than decoration, which is what lets an exclusion be bound to
// a change instead of to an object. Refusing the DROP of a table today must not also suppress an ALTER
// of it tomorrow — that would be a different decision, taken on the user's behalf.
public static class SchemaChangeUnits
{
    public static IReadOnlyList<ChangeUnit> From(SchemaDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        return diff.Differing
            .Select(o => new { Object = o, Id = IdOf(o) })
            .Where(x => x.Id is not null)
            .Select(x => new ChangeUnit { Id = x.Id!, Description = Describe(x.Id!) })
            .ToList();
    }

    public static ChangeUnitId? IdOf(ObjectDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        return KindOf(diff.Kind) is { } kind ? new ChangeUnitId(diff.Identity, kind) : null;
    }

    public static ChangeUnitKind? KindOf(DiffKind kind) => kind switch
    {
        DiffKind.SourceOnly => ChangeUnitKind.CreateObject,
        DiffKind.TargetOnly => ChangeUnitKind.DropObject,
        DiffKind.Different => ChangeUnitKind.AlterObject,
        _ => null
    };

    public static string Describe(ChangeUnitId id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var verb = id.Kind switch
        {
            ChangeUnitKind.CreateObject => "create",
            ChangeUnitKind.DropObject => "drop",
            _ => "alter"
        };

        return $"{verb} {id.Object.Type.ToString().ToLowerInvariant()} {id.Object.QualifiedName}";
    }
}
