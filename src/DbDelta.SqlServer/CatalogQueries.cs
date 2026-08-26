namespace DbDelta.SqlServer;

internal static class CatalogQueries
{
    public const string Probe = """
        SELECT
            CONVERT(nvarchar(256), SERVERPROPERTY('ServerName'))                    AS [ServerName],
            DB_NAME()                                                               AS [DatabaseName],
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))                AS [ProductVersion],
            CONVERT(nvarchar(128), SERVERPROPERTY('Edition'))                       AS [Edition],
            CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))      AS [Collation];
        """;

    public const string Columns = """
        SELECT
            s.name              AS [SchemaName],
            t.name              AS [TableName],
            c.name              AS [ColumnName],
            c.column_id         AS [OrdinalPosition],
            ty.name             AS [TypeName],
            c.max_length        AS [MaxLength],
            c.precision         AS [Precision],
            c.scale             AS [Scale],
            c.is_nullable       AS [IsNullable],
            c.collation_name    AS [CollationName],
            ic.seed_value       AS [SeedValue],
            ic.increment_value  AS [IncrementValue],
            cc.definition       AS [ComputedDefinition],
            dc.definition       AS [DefaultDefinition],
            dc.name             AS [DefaultConstraintName]
        FROM sys.tables t
        JOIN sys.schemas s          ON s.schema_id = t.schema_id
        JOIN sys.columns c          ON c.object_id = t.object_id
        JOIN sys.types ty           ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc
               ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name, c.column_id;
        """;

    public const string KeyConstraints = """
        SELECT
            s.name                  AS [SchemaName],
            t.name                  AS [TableName],
            kc.name                 AS [ConstraintName],
            kc.type                 AS [ConstraintType],
            i.type_desc             AS [IndexType],
            c.name                  AS [ColumnName],
            ic.key_ordinal          AS [KeyOrdinal],
            ic.is_descending_key    AS [IsDescending]
        FROM sys.key_constraints kc
        JOIN sys.tables t       ON t.object_id = kc.parent_object_id
        JOIN sys.schemas s      ON s.schema_id = t.schema_id
        JOIN sys.indexes i      ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0 AND ic.is_included_column = 0
        ORDER BY s.name, t.name, kc.name, ic.key_ordinal;
        """;

    // Indexes backing a PK or unique constraint are reported through KeyConstraints instead, so they
    // are filtered out here to avoid the same object appearing twice under different names.
    public const string Indexes = """
        SELECT
            s.name                  AS [SchemaName],
            t.name                  AS [TableName],
            i.name                  AS [IndexName],
            i.is_unique             AS [IsUnique],
            i.type_desc             AS [IndexType],
            i.filter_definition     AS [FilterDefinition],
            i.fill_factor           AS [FillFactor],
            c.name                  AS [ColumnName],
            ic.is_included_column   AS [IsIncluded],
            ic.key_ordinal          AS [KeyOrdinal],
            ic.is_descending_key    AS [IsDescending]
        FROM sys.indexes i
        JOIN sys.tables t       ON t.object_id = i.object_id
        JOIN sys.schemas s      ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0
          AND i.is_primary_key = 0
          AND i.is_unique_constraint = 0
          AND i.type <> 0
          AND i.name IS NOT NULL
        ORDER BY s.name, t.name, i.name, ic.is_included_column, ic.key_ordinal, c.name;
        """;

    public const string ForeignKeys = """
        SELECT
            s.name                          AS [SchemaName],
            t.name                          AS [TableName],
            fk.name                         AS [ForeignKeyName],
            rs.name                         AS [ReferencedSchema],
            rt.name                         AS [ReferencedTable],
            fk.delete_referential_action    AS [DeleteAction],
            fk.update_referential_action    AS [UpdateAction],
            fk.is_disabled                  AS [IsDisabled],
            pc.name                         AS [ColumnName],
            rc.name                         AS [ReferencedColumn],
            fkc.constraint_column_id        AS [ColumnOrdinal]
        FROM sys.foreign_keys fk
        JOIN sys.tables t   ON t.object_id = fk.parent_object_id
        JOIN sys.schemas s  ON s.schema_id = t.schema_id
        JOIN sys.tables rt  ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name, fk.name, fkc.constraint_column_id;
        """;

    public const string CheckConstraints = """
        SELECT
            s.name          AS [SchemaName],
            t.name          AS [TableName],
            cc.name         AS [ConstraintName],
            cc.definition   AS [Definition],
            cc.is_disabled  AS [IsDisabled]
        FROM sys.check_constraints cc
        JOIN sys.tables t   ON t.object_id = cc.parent_object_id
        JOIN sys.schemas s  ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name, cc.name;
        """;

    public const string Views = """
        SELECT s.name AS [SchemaName], v.name AS [ViewName], m.definition AS [Definition]
        FROM sys.views v
        JOIN sys.schemas s      ON s.schema_id = v.schema_id
        JOIN sys.sql_modules m  ON m.object_id = v.object_id
        WHERE v.is_ms_shipped = 0
        ORDER BY s.name, v.name;
        """;

    public const string Routines = """
        SELECT s.name AS [SchemaName], o.name AS [RoutineName], o.type AS [RoutineType], m.definition AS [Definition]
        FROM sys.objects o
        JOIN sys.schemas s      ON s.schema_id = o.schema_id
        JOIN sys.sql_modules m  ON m.object_id = o.object_id
        WHERE o.is_ms_shipped = 0 AND o.type IN ('P', 'FN', 'IF', 'TF')
        ORDER BY s.name, o.name;
        """;

    public const string Triggers = """
        SELECT
            s.name          AS [SchemaName],
            tr.name         AS [TriggerName],
            t.name          AS [TableName],
            tr.is_disabled  AS [IsDisabled],
            m.definition    AS [Definition]
        FROM sys.triggers tr
        JOIN sys.tables t       ON t.object_id = tr.parent_id
        JOIN sys.schemas s      ON s.schema_id = t.schema_id
        JOIN sys.sql_modules m  ON m.object_id = tr.object_id
        WHERE tr.is_ms_shipped = 0 AND tr.parent_class = 1
        ORDER BY s.name, tr.name;
        """;

    public const string Sequences = """
        SELECT
            s.name                              AS [SchemaName],
            sq.name                             AS [SequenceName],
            ty.name                             AS [TypeName],
            sq.precision                        AS [Precision],
            sq.scale                            AS [Scale],
            CONVERT(bigint, sq.start_value)     AS [StartValue],
            CONVERT(bigint, sq.increment)       AS [Increment],
            CONVERT(bigint, sq.minimum_value)   AS [MinimumValue],
            CONVERT(bigint, sq.maximum_value)   AS [MaximumValue],
            sq.is_cycling                       AS [IsCycling]
        FROM sys.sequences sq
        JOIN sys.schemas s  ON s.schema_id = sq.schema_id
        JOIN sys.types ty   ON ty.user_type_id = sq.user_type_id
        ORDER BY s.name, sq.name;
        """;
}
