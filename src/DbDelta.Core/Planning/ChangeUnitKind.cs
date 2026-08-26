namespace DbDelta.Core.Planning;

public enum ChangeUnitKind
{
    CreateObject,
    AlterObject,
    DropObject,
    InsertRow,
    UpdateRow,
    DeleteRow
}
