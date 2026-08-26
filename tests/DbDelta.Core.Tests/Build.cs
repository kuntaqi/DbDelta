using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

internal static class Build
{
    public static ObjectIdentity TableId(string name, string schema = "dbo") =>
        new(ObjectType.Table, schema, name);

    public static ColumnDefinition Column(
        string name,
        string type = "NVARCHAR",
        int? length = null,
        bool nullable = false) =>
        new()
        {
            Name = name,
            DataType = new DataTypeSpec(type, length),
            IsNullable = nullable
        };

    public static ForeignKeyDefinition ForeignKey(string name, string column, string referencedTable) =>
        new()
        {
            Name = name,
            Columns = [column],
            ReferencedTable = TableId(referencedTable),
            ReferencedColumns = ["Id"]
        };

    public static TableDefinition Table(
        string name,
        IReadOnlyList<ColumnDefinition>? columns = null,
        IReadOnlyList<ForeignKeyDefinition>? foreignKeys = null,
        IReadOnlyList<IndexDefinition>? indexes = null,
        string schema = "dbo") =>
        new()
        {
            Identity = TableId(name, schema),
            Columns = columns ?? [Column("Id", "INT")],
            ForeignKeys = foreignKeys ?? [],
            Indexes = indexes ?? []
        };

    public static ViewDefinition View(string name, string definition, string schema = "dbo") =>
        new()
        {
            Identity = new ObjectIdentity(ObjectType.View, schema, name),
            Definition = definition
        };

    public static DatabaseSchema Schema(
        string databaseName,
        IReadOnlyList<TableDefinition>? tables = null,
        IReadOnlyList<ViewDefinition>? views = null) =>
        new()
        {
            DatabaseName = databaseName,
            Tables = tables ?? [],
            Views = views ?? []
        };
}
