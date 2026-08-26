using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

public sealed record PlanSelection
{
    private PlanSelection(SelectionScope scope, ObjectIdentity? obj, string? rowKey)
    {
        Scope = scope;
        Object = obj;
        RowKey = rowKey;
    }

    public SelectionScope Scope { get; }

    public ObjectIdentity? Object { get; }

    public string? RowKey { get; }

    public static PlanSelection Database() => new(SelectionScope.Database, null, null);

    public static PlanSelection Table(ObjectIdentity table) =>
        new(SelectionScope.Table, table, null);

    public static PlanSelection Row(ObjectIdentity table, string rowKey) =>
        new(SelectionScope.Row, table, rowKey);

    public bool Covers(ChangeUnitId unit) => Scope switch
    {
        SelectionScope.Database => true,
        SelectionScope.Table => Object == unit.Object,
        SelectionScope.Row => Object == unit.Object && unit.RowKey == RowKey,
        _ => false
    };

    public override string ToString() => Scope switch
    {
        SelectionScope.Database => "database",
        SelectionScope.Table => Object!.QualifiedName,
        _ => $"{Object!.QualifiedName}[{RowKey}]"
    };
}
