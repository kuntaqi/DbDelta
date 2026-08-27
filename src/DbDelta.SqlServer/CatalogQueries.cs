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
            ty.is_user_defined  AS [IsUserDefined],
            tys.name            AS [TypeSchemaName],
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
        JOIN sys.schemas tys        ON tys.schema_id = ty.schema_id
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

    // Views and routines depend on each other, and on tables, through their SQL bodies. Reading the
    // catalog is the only reliable way to learn that without parsing T-SQL; referenced_id is null when
    // the reference cannot be resolved, so those rows are skipped rather than guessed at.
    //
    // Tables are on the referenced side because a view is not only ordered against what it reads, it
    // cannot be created at all if the table it selects from is missing. Emission order never needed
    // them — tables are created in an earlier phase regardless — but closure does.
    public const string ProgrammableDependencies = """
        SELECT DISTINCT
            s.name  AS [SchemaName],
            o.name  AS [Name],
            rs.name AS [ReferencedSchema],
            ro.name AS [ReferencedName],
            ro.type AS [ReferencedType]
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects o  ON o.object_id = d.referencing_id
        JOIN sys.schemas s  ON s.schema_id = o.schema_id
        JOIN sys.objects ro ON ro.object_id = d.referenced_id
        JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
        WHERE o.is_ms_shipped = 0
          AND ro.is_ms_shipped = 0
          AND o.type IN ('V', 'P', 'FN', 'IF', 'TF')
          AND ro.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
          AND o.object_id <> ro.object_id;
        """;

    // Alias and CLR types together, told apart by is_assembly_type. A CLR type is read so it can be
    // reported rather than emitted: syncing one means syncing the assembly behind it.
    //
    // system_type_id is joined back to sys.types to name the base type, and the join needs
    // user_type_id = system_type_id to land on the built-in row rather than another alias of it.
    public const string ScalarTypes = """
        SELECT
            s.name              AS [SchemaName],
            t.name              AS [TypeName],
            bt.name             AS [BaseTypeName],
            t.max_length        AS [MaxLength],
            t.precision         AS [Precision],
            t.scale             AS [Scale],
            t.is_nullable       AS [IsNullable],
            t.is_assembly_type  AS [IsAssemblyType]
        FROM sys.types t
        JOIN sys.schemas s      ON s.schema_id = t.schema_id
        LEFT JOIN sys.types bt  ON bt.user_type_id = t.system_type_id AND bt.is_user_defined = 0
        WHERE t.is_user_defined = 1 AND t.is_table_type = 0
        ORDER BY s.name, t.name;
        """;

    public const string TableTypes = """
        SELECT
            s.name              AS [SchemaName],
            tt.name             AS [TypeName],
            c.name              AS [ColumnName],
            c.column_id         AS [OrdinalPosition],
            ty.name             AS [ColumnTypeName],
            c.max_length        AS [MaxLength],
            c.precision         AS [Precision],
            c.scale             AS [Scale],
            c.is_nullable       AS [IsNullable],
            ty.is_user_defined  AS [IsUserDefined],
            tys.name            AS [TypeSchemaName]
        FROM sys.table_types tt
        JOIN sys.schemas s      ON s.schema_id = tt.schema_id
        JOIN sys.columns c      ON c.object_id = tt.type_table_object_id
        JOIN sys.types ty       ON ty.user_type_id = c.user_type_id
        JOIN sys.schemas tys    ON tys.schema_id = ty.schema_id
        WHERE tt.is_user_defined = 1
        ORDER BY s.name, tt.name, c.column_id;
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
