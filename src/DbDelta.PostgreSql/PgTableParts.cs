using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

// Mutable gather-point for one table while the reader folds flat catalog rows, built into an immutable
// TableDefinition at the end — the same accumulate-then-build pattern the SQL Server reader uses.
internal sealed class PgTableParts
{
    public PgTableParts(ObjectIdentity identity) => Identity = identity;

    public ObjectIdentity Identity { get; }

    public List<ColumnDefinition> Columns { get; } = [];

    public PrimaryKeyDefinition? PrimaryKey { get; set; }

    public List<UniqueConstraintDefinition> UniqueConstraints { get; } = [];

    public List<ForeignKeyDefinition> ForeignKeys { get; } = [];

    public List<CheckConstraintDefinition> CheckConstraints { get; } = [];

    public List<IndexDefinition> Indexes { get; } = [];

    public TableDefinition Build() => new()
    {
        Identity = Identity,
        Columns = Columns,
        PrimaryKey = PrimaryKey,
        UniqueConstraints = UniqueConstraints,
        ForeignKeys = ForeignKeys,
        CheckConstraints = CheckConstraints,
        Indexes = Indexes,
    };
}
