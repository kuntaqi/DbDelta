using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

internal sealed class TableAccumulator
{
    public TableAccumulator(ObjectIdentity identity) => Identity = identity;

    public ObjectIdentity Identity { get; }

    public List<ColumnDefinition> Columns { get; } = [];

    public PrimaryKeyDefinition? PrimaryKey { get; set; }

    public List<UniqueConstraintDefinition> UniqueConstraints { get; } = [];

    public List<IndexDefinition> Indexes { get; } = [];

    public List<ForeignKeyDefinition> ForeignKeys { get; } = [];

    public List<CheckConstraintDefinition> CheckConstraints { get; } = [];

    public TableDefinition Build() => new()
    {
        Identity = Identity,
        Columns = Columns,
        PrimaryKey = PrimaryKey,
        UniqueConstraints = UniqueConstraints,
        Indexes = Indexes,
        ForeignKeys = ForeignKeys,
        CheckConstraints = CheckConstraints
    };
}
