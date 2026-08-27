using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

internal static class TSqlWriter
{
    private static readonly SqlServerQuoter Q = SqlServerQuoter.Instance;

    public static string ColumnDefinition(ColumnDefinition column, bool includeDefault)
    {
        if (column.ComputedExpression is not null)
        {
            return $"{Q.Quote(column.Name)} AS {column.ComputedExpression}";
        }

        var text = $"{Q.Quote(column.Name)} {SqlTypeText.Declare(column.DataType)}";

        if (column.Identity is not null)
        {
            text += $" IDENTITY({column.Identity.Seed},{column.Identity.Increment})";
        }

        text += column.IsNullable ? " NULL" : " NOT NULL";

        if (includeDefault && column.DefaultExpression is not null && column.DefaultConstraintName is not null)
        {
            text += $" CONSTRAINT {Q.Quote(column.DefaultConstraintName)} DEFAULT {column.DefaultExpression}";
        }

        return text;
    }

    public static string CreateTable(TableDefinition table)
    {
        var columns = table.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => "    " + ColumnDefinition(c, includeDefault: true));

        return $"CREATE TABLE {Q.Qualify(table.Identity)} (\n{string.Join(",\n", columns)}\n);";
    }

    public static string AddPrimaryKey(ObjectIdentity table, PrimaryKeyDefinition key) =>
        $"ALTER TABLE {Q.Qualify(table)} ADD CONSTRAINT {Q.Quote(key.Name)} PRIMARY KEY "
        + $"{(key.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(key.Columns)});";

    public static string AddUniqueConstraint(ObjectIdentity table, UniqueConstraintDefinition constraint) =>
        $"ALTER TABLE {Q.Qualify(table)} ADD CONSTRAINT {Q.Quote(constraint.Name)} UNIQUE "
        + $"{(constraint.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(constraint.Columns)});";

    public static string DropConstraint(ObjectIdentity table, string name) =>
        $"ALTER TABLE {Q.Qualify(table)} DROP CONSTRAINT {Q.Quote(name)};";

    public static string CreateIndex(ObjectIdentity table, IndexDefinition index)
    {
        var text = index.IsUnique ? "CREATE UNIQUE " : "CREATE ";
        text += index.IsClustered ? "CLUSTERED INDEX " : "INDEX ";
        text += $"{Q.Quote(index.Name)} ON {Q.Qualify(table)} ({KeyColumns(index.Columns)})";

        if (index.IncludedColumns.Count > 0)
        {
            text += $" INCLUDE ({string.Join(", ", index.IncludedColumns.Select(Q.Quote))})";
        }

        if (index.FilterExpression is not null)
        {
            text += $" WHERE {index.FilterExpression}";
        }

        return text + ";";
    }

    public static string DropIndex(ObjectIdentity table, string name) =>
        $"DROP INDEX {Q.Quote(name)} ON {Q.Qualify(table)};";

    public static string AddColumn(ObjectIdentity table, ColumnDefinition column) =>
        $"ALTER TABLE {Q.Qualify(table)} ADD {ColumnDefinition(column, includeDefault: true)};";

    public static string DropColumn(ObjectIdentity table, string name) =>
        $"ALTER TABLE {Q.Qualify(table)} DROP COLUMN {Q.Quote(name)};";

    public static string AlterColumn(ObjectIdentity table, ColumnDefinition column) =>
        $"ALTER TABLE {Q.Qualify(table)} ALTER COLUMN {ColumnDefinition(column, includeDefault: false)};";

    public static string AddCheckConstraint(ObjectIdentity table, CheckConstraintDefinition check) =>
        $"ALTER TABLE {Q.Qualify(table)} ADD CONSTRAINT {Q.Quote(check.Name)} CHECK {check.Expression};";

    public static string AddForeignKey(ObjectIdentity table, ForeignKeyDefinition key)
    {
        var text = $"ALTER TABLE {Q.Qualify(table)} ADD CONSTRAINT {Q.Quote(key.Name)} FOREIGN KEY "
            + $"({string.Join(", ", key.Columns.Select(Q.Quote))}) REFERENCES {Q.Qualify(key.ReferencedTable)} "
            + $"({string.Join(", ", key.ReferencedColumns.Select(Q.Quote))})";

        if (key.OnDelete != ReferentialAction.NoAction)
        {
            text += $" ON DELETE {Action(key.OnDelete)}";
        }

        if (key.OnUpdate != ReferentialAction.NoAction)
        {
            text += $" ON UPDATE {Action(key.OnUpdate)}";
        }

        return text + ";";
    }

    public static string DropTable(ObjectIdentity table) =>
        $"DROP TABLE {Q.Qualify(table)};";

    public static string DropProgrammable(ObjectIdentity identity, string keyword) =>
        $"DROP {keyword} {Q.Qualify(identity)};";

    // CREATE VIEW / PROCEDURE / FUNCTION / TRIGGER / SCHEMA must each be the first statement in their
    // batch, and this script is deliberately one transaction with no GO separators. Wrapping in
    // sp_executesql gives each its own batch without breaking the transaction.
    public static string ExecuteAsBatch(string sql) =>
        $"EXEC sp_executesql N'{sql.Replace("'", "''", StringComparison.Ordinal)}';";

    public static string CreateSchemaIfMissing(string schema) =>
        $"IF SCHEMA_ID('{schema.Replace("'", "''", StringComparison.Ordinal)}') IS NULL\n"
        + $"    EXEC sp_executesql N'CREATE SCHEMA {Q.Quote(schema)}';";

    // Converts the stored CREATE to CREATE OR ALTER so the same text works whether or not the object
    // is already there, which keeps create and update on one path.
    //
    // The CREATE is rarely the first thing in the text. A body scripted by SSMS opens with
    // /****** Object: … Script Date: … ******/, and plenty are hand-written with a -- note above them; this
    // used to test the very first characters for "CREATE", miss, and emit a plain CREATE. That works against
    // an empty target and fails against an object that already exists, taking the transaction with it.
    // Measured against one real database: 38 of 521 programmables, 7%.
    //
    // The comments are kept rather than stripped — they are part of the definition the source holds — and
    // OR ALTER goes in after them. A comment is not a statement, so CREATE OR ALTER is still the first one
    // in its batch.
    public static string CreateOrAlter(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var start = FirstStatement(definition);
        const string create = "CREATE";

        if (start + create.Length > definition.Length
            || !definition.AsSpan(start, create.Length).Equals(create, StringComparison.OrdinalIgnoreCase))
        {
            return definition.TrimStart();
        }

        var cut = start + create.Length;

        return string.Concat(definition.AsSpan(0, cut), " OR ALTER", definition.AsSpan(cut));
    }

    // Index of the first thing that is not whitespace or a comment. T-SQL block comments nest, so the
    // depth is counted rather than scanning for the first "*/".
    private static int FirstStatement(string text)
    {
        var i = 0;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '-' && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                var depth = 1;
                i += 2;

                while (i < text.Length && depth > 0)
                {
                    if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }

                continue;
            }

            break;
        }

        return i;
    }

    public static string ReseedIdentity(ObjectIdentity table, string column, long value) =>
        $"DBCC CHECKIDENT ('{table.QualifiedName}', RESEED, {value}) WITH NO_INFOMSGS; -- {column}";

    private static string KeyColumns(IEnumerable<IndexColumn> columns) =>
        string.Join(", ", columns.Select(c => $"{Q.Quote(c.Name)}{(c.IsDescending ? " DESC" : " ASC")}"));

    private static string Action(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION"
    };
}
