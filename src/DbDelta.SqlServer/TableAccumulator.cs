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

    public List<StatisticsDefinition> Statistics { get; } = [];

    public List<SchemaBoundReference> SchemaBoundReferences { get; } = [];

    public Dictionary<string, string?> Extras { get; } = [];

    public TableDefinition Build() => new()
    {
        Identity = Identity,
        Columns = Columns,
        PrimaryKey = PrimaryKey,
        UniqueConstraints = UniqueConstraints,
        Indexes = Indexes,
        ForeignKeys = ForeignKeys,
        CheckConstraints = CheckConstraints,
        Statistics = Statistics,
        SchemaBoundReferences = SchemaBoundReferences,
        Extras = Extras.Count == 0 ? ProviderExtras.Empty : new ProviderExtras(Extras)
    };
}
