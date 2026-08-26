namespace DbDelta.Core.Model;

public enum ObjectType
{
    Schema,
    Table,
    View,
    Routine,
    Trigger,
    Sequence,
    UserDefinedType,
    Column,
    PrimaryKey,
    UniqueConstraint,
    Index,
    ForeignKey,
    CheckConstraint,
    DefaultConstraint
}
