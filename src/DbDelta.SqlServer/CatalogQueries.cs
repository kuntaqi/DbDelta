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
    // Two branches, because sys.sql_expression_dependencies numbers its referenced entities in more than one
    // id space and says which one in referenced_class. Class 1 is an object and referenced_id is an
    // object_id; class 6 is a type and referenced_id is a *user_type_id*. Joining every row to
    // sys.objects — which is what this did — silently dropped every type a routine referred to, and was
    // comparing ids from two different spaces to do it. Nothing collided only because a user_type_id lands
    // where the system objects live and those are filtered out by is_ms_shipped.
    //
    // What the second branch buys: a procedure taking a table-valued parameter depends on that table type,
    // and a plan containing the procedure without the type produces a CREATE that cannot compile. The same
    // is true of an alias type named anywhere in a body. Both were invisible before.
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
        WHERE d.referenced_class = 1
          AND o.is_ms_shipped = 0
          AND ro.is_ms_shipped = 0
          AND o.type IN ('V', 'P', 'FN', 'IF', 'TF')
          AND ro.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
          AND o.object_id <> ro.object_id

        UNION

        SELECT DISTINCT
            s.name      AS [SchemaName],
            o.name      AS [Name],
            rts.name    AS [ReferencedSchema],
            rt.name     AS [ReferencedName],
            'TY'        AS [ReferencedType]
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects o   ON o.object_id = d.referencing_id
        JOIN sys.schemas s   ON s.schema_id = o.schema_id
        JOIN sys.types rt    ON rt.user_type_id = d.referenced_id
        JOIN sys.schemas rts ON rts.schema_id = rt.schema_id
        WHERE d.referenced_class = 6
          AND o.is_ms_shipped = 0
          AND o.type IN ('V', 'P', 'FN', 'IF', 'TF')
          AND rt.is_user_defined = 1;
        """;

    // Every user database on the instance, from one connection. sys.master_files carries the file sizes for
    // all of them, so size costs nothing extra — and size in pages, hence the * 8192.
    //
    // database_id > 4 drops master, model, msdb and tempdb: none of them is something this tool would ever
    // sync. source_database_id IS NULL drops snapshots, which are a view of another database rather than one
    // of their own. HAS_DBACCESS answers without connecting, so a database that cannot be opened is still
    // listed and still says so.
    public const string Databases = """
        SELECT
            d.name                  AS [Name],
            d.state_desc            AS [State],
            d.recovery_model_desc   AS [RecoveryModel],
            d.is_read_only          AS [IsReadOnly],
            CONVERT(bit, ISNULL(HAS_DBACCESS(d.name), 0)) AS [Accessible],
            ISNULL(SUM(CASE WHEN mf.type = 0 THEN CONVERT(bigint, mf.size) ELSE 0 END), 0) * 8192 AS [DataBytes],
            ISNULL(SUM(CASE WHEN mf.type = 1 THEN CONVERT(bigint, mf.size) ELSE 0 END), 0) * 8192 AS [LogBytes]
        FROM sys.databases d
        LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
        WHERE d.database_id > 4 AND d.source_database_id IS NULL
        GROUP BY d.name, d.state_desc, d.recovery_model_desc, d.is_read_only, d.database_id
        ORDER BY d.name;
        """;

    // The same list without sizes, for a login that can see sys.databases but not sys.master_files. Losing
    // the sizes is worth far less than losing the list.
    public const string DatabasesWithoutSizes = """
        SELECT
            d.name                  AS [Name],
            d.state_desc            AS [State],
            d.recovery_model_desc   AS [RecoveryModel],
            d.is_read_only          AS [IsReadOnly],
            CONVERT(bit, ISNULL(HAS_DBACCESS(d.name), 0)) AS [Accessible],
            CONVERT(bigint, 0)      AS [DataBytes],
            CONVERT(bigint, 0)      AS [LogBytes]
        FROM sys.databases d
        WHERE d.database_id > 4 AND d.source_database_id IS NULL
        ORDER BY d.name;
        """;

    // Run while connected to the database in question. DATABASEPROPERTYEX answers correctly for DB_NAME()
    // where it returns NULL for a column of sys.databases, which is the reason collation costs a connection.
    public const string DatabaseDetail = """
        SELECT
            CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS [Collation],
            (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0)          AS [Tables],
            (SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0)           AS [Views],
            (SELECT COUNT(*) FROM sys.objects
              WHERE type IN ('P', 'FN', 'IF', 'TF') AND is_ms_shipped = 0)     AS [Routines];
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

    // A table type is backed by an object, so its columns and constraints live in the same catalogs a
    // table's do — reached through type_table_object_id rather than through sys.tables.
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
            tys.name            AS [TypeSchemaName],
            dc.definition       AS [DefaultDefinition],
            cc.definition       AS [ComputedDefinition]
        FROM sys.table_types tt
        JOIN sys.schemas s      ON s.schema_id = tt.schema_id
        JOIN sys.columns c      ON c.object_id = tt.type_table_object_id
        JOIN sys.types ty       ON ty.user_type_id = c.user_type_id
        JOIN sys.schemas tys    ON tys.schema_id = ty.schema_id
        LEFT JOIN sys.default_constraints dc
               ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.computed_columns cc
               ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE tt.is_user_defined = 1
        ORDER BY s.name, tt.name, c.column_id;
        """;

    // PRIMARY KEY and UNIQUE inside a table type. The constraint name carries a random suffix, so it is read
    // but never compared; the columns and clustering are the parts that mean anything.
    public const string TableTypeKeys = """
        SELECT
            s.name                  AS [SchemaName],
            tt.name                 AS [TypeName],
            kc.name                 AS [ConstraintName],
            kc.type                 AS [ConstraintType],
            i.type_desc             AS [IndexType],
            c.name                  AS [ColumnName],
            ic.key_ordinal          AS [KeyOrdinal],
            ic.is_descending_key    AS [IsDescending]
        FROM sys.table_types tt
        JOIN sys.schemas s          ON s.schema_id = tt.schema_id
        JOIN sys.key_constraints kc ON kc.parent_object_id = tt.type_table_object_id
        JOIN sys.indexes i          ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        JOIN sys.index_columns ic   ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c          ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE tt.is_user_defined = 1 AND ic.is_included_column = 0
        ORDER BY s.name, tt.name, kc.name, ic.key_ordinal;
        """;

    public const string TableTypeChecks = """
        SELECT
            s.name          AS [SchemaName],
            tt.name         AS [TypeName],
            cc.name         AS [ConstraintName],
            cc.definition   AS [Definition]
        FROM sys.table_types tt
        JOIN sys.schemas s              ON s.schema_id = tt.schema_id
        JOIN sys.check_constraints cc   ON cc.parent_object_id = tt.type_table_object_id
        WHERE tt.is_user_defined = 1
        ORDER BY s.name, tt.name, cc.definition;
        """;

    // Standalone indexes only. The indexes backing a PRIMARY KEY or a UNIQUE constraint are already reported
    // as those constraints, and counting them twice would emit each one twice.
    public const string TableTypeIndexes = """
        SELECT
            s.name                  AS [SchemaName],
            tt.name                 AS [TypeName],
            i.name                  AS [IndexName],
            i.is_unique             AS [IsUnique],
            i.type_desc             AS [IndexType],
            c.name                  AS [ColumnName],
            ic.key_ordinal          AS [KeyOrdinal],
            ic.is_descending_key    AS [IsDescending],
            ic.is_included_column   AS [IsIncluded]
        FROM sys.table_types tt
        JOIN sys.schemas s          ON s.schema_id = tt.schema_id
        JOIN sys.indexes i          ON i.object_id = tt.type_table_object_id
        JOIN sys.index_columns ic   ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c          ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE tt.is_user_defined = 1
          AND i.is_primary_key = 0
          AND i.is_unique_constraint = 0
          AND i.name IS NOT NULL
        ORDER BY s.name, tt.name, i.name, ic.is_included_column, ic.key_ordinal;
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
