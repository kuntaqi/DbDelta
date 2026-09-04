using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

internal static class TSqlWriter
{
    private static readonly SqlServerQuoter Q = SqlServerQuoter.Instance;

    public static string ColumnDefinition(
        ColumnDefinition column,
        bool includeDefault,
        string? databaseCollation = null)
    {
        if (column.ComputedExpression is not null)
        {
            return $"{Q.Quote(column.Name)} AS {column.ComputedExpression}";
        }

        // COLLATE belongs immediately after the type, ahead of IDENTITY and the nullability — which costs
        // nothing here, since an identity column is never a string.
        var text = $"{Q.Quote(column.Name)} {SqlTypeText.Declare(column.DataType)}"
            + CollationClause(column, databaseCollation);

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

    public static string CreateTable(TableDefinition table, string? databaseCollation = null)
    {
        var columns = table.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => "    " + ColumnDefinition(c, includeDefault: true, databaseCollation));

        return $"CREATE TABLE {Q.Qualify(table.Identity)} (\n{string.Join(",\n", columns)}\n);";
    }

    // A column takes the collation of the database it is created in unless the statement says otherwise,
    // so the clause is written exactly when the source column would not get what it needs for free.
    //
    // Checked against a server rather than assumed, because the rule for ALTER is the one that could have
    // gone either way: ALTER COLUMN with no COLLATE resets the column to the database default — it does
    // not keep what the column already had. So omitting the clause is a statement about the result, not a
    // saving on noise, and a script that omits it everywhere would quietly recollate half a database.
    //
    // Where the target's own default is unknown, the clause goes in regardless. Being verbose is a
    // reviewing cost; being wrong is a data one.
    private static string CollationClause(ColumnDefinition column, string? databaseCollation)
    {
        if (column.Collation is null
            || (databaseCollation is not null
                && string.Equals(column.Collation, databaseCollation, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        // A collation name cannot be bracket-quoted the way an identifier can, so it goes into the
        // statement as written. It comes from the server's own catalog, and this says so out loud rather
        // than trusting that quietly.
        if (!column.Collation.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException(
                $"{column.Name} reports a collation that is not a plain name ({column.Collation}), and a "
                + "collation cannot be quoted in T-SQL.");
        }

        return $" COLLATE {column.Collation}";
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
        ArgumentNullException.ThrowIfNull(index);

        return index.Kind switch
        {
            IndexKind.Columnstore => CreateColumnstoreIndex(table, index),
            IndexKind.Xml => CreateXmlIndex(table, index),
            IndexKind.Spatial => CreateSpatialIndex(table, index),
            _ => CreateRowstoreIndex(table, index)
        };
    }

    private static string CreateRowstoreIndex(ObjectIdentity table, IndexDefinition index)
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

    // A clustered columnstore covers the whole table and its DDL names no columns — except an ordered one,
    // whose ORDER list is the only thing it does state, which is why the reader puts exactly those columns
    // on it and nothing else. A nonclustered one states the columns it holds; there is no ASC or DESC on
    // any of it, because a columnstore has no key order beyond that ORDER clause.
    private static string CreateColumnstoreIndex(ObjectIdentity table, IndexDefinition index)
    {
        if (index.IsClustered)
        {
            var order = index.Columns.Count == 0
                ? string.Empty
                : $" ORDER ({string.Join(", ", index.Columns.Select(c => Q.Quote(c.Name)))})";

            return $"CREATE CLUSTERED COLUMNSTORE INDEX {Q.Quote(index.Name)} ON {Q.Qualify(table)}{order};";
        }

        var text = $"CREATE NONCLUSTERED COLUMNSTORE INDEX {Q.Quote(index.Name)} ON {Q.Qualify(table)} "
            + $"({string.Join(", ", index.Columns.Select(c => Q.Quote(c.Name)))})";

        if (index.FilterExpression is not null)
        {
            text += $" WHERE {index.FilterExpression}";
        }

        return text + ";";
    }

    private static string CreateXmlIndex(ObjectIdentity table, IndexDefinition index)
    {
        var column = Q.Quote(index.Columns[0].Name);

        if (!string.Equals(index.Extras[XmlExtras.Kind], XmlExtras.Secondary, StringComparison.OrdinalIgnoreCase))
        {
            return $"CREATE PRIMARY XML INDEX {Q.Quote(index.Name)} ON {Q.Qualify(table)} ({column});";
        }

        return $"CREATE XML INDEX {Q.Quote(index.Name)} ON {Q.Qualify(table)} ({column}) "
            + $"USING XML INDEX {Q.Quote(index.Extras[XmlExtras.PrimaryIndex]!)} "
            + $"FOR {index.Extras[XmlExtras.SecondaryType]};";
    }

    // GRIDS and BOUNDING_BOX are each carried only when the scheme has them: an AUTO_GRID scheme rejects
    // a GRIDS clause, and only a geometry grid takes a bounding box — which it also requires.
    private static string CreateSpatialIndex(ObjectIdentity table, IndexDefinition index)
    {
        var options = new List<string>();

        if (index.Extras[SpatialExtras.BoundingBox] is { } box)
        {
            options.Add($"BOUNDING_BOX = ({box})");
        }

        if (index.Extras[SpatialExtras.Grids] is { } grids)
        {
            options.Add($"GRIDS = ({grids})");
        }

        if (index.Extras[SpatialExtras.CellsPerObject] is { } cells)
        {
            options.Add($"CELLS_PER_OBJECT = {cells}");
        }

        var text = $"CREATE SPATIAL INDEX {Q.Quote(index.Name)} ON {Q.Qualify(table)} "
            + $"({Q.Quote(index.Columns[0].Name)}) USING {index.Extras[SpatialExtras.Scheme]}";

        return options.Count == 0 ? text + ";" : $"{text} WITH ({string.Join(", ", options)});";
    }

    public static string DropIndex(ObjectIdentity table, string name) =>
        $"DROP INDEX {Q.Quote(name)} ON {Q.Qualify(table)};";

    public static string AddColumn(
        ObjectIdentity table,
        ColumnDefinition column,
        string? databaseCollation = null) =>
        $"ALTER TABLE {Q.Qualify(table)} ADD "
        + $"{ColumnDefinition(column, includeDefault: true, databaseCollation)};";

    public static string DropColumn(ObjectIdentity table, string name) =>
        $"ALTER TABLE {Q.Qualify(table)} DROP COLUMN {Q.Quote(name)};";

    public static string AlterColumn(
        ObjectIdentity table,
        ColumnDefinition column,
        string? databaseCollation = null) =>
        $"ALTER TABLE {Q.Qualify(table)} ALTER COLUMN "
        + $"{ColumnDefinition(column, includeDefault: false, databaseCollation)};";

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

        var start = ProgrammableHeaderReader.FirstStatement(definition);
        const string create = "CREATE";

        if (start + create.Length > definition.Length
            || !definition.AsSpan(start, create.Length).Equals(create, StringComparison.OrdinalIgnoreCase))
        {
            return definition.TrimStart();
        }

        var cut = start + create.Length;

        return string.Concat(definition.AsSpan(0, cut), " OR ALTER", definition.AsSpan(cut));
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
