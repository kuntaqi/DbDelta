using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerSchemaReader : ISchemaReader
{
    private const int PermissionDenied = 229;
    private const int ObjectNotFound = 208;

    private readonly string _connectionString;

    // Set when a non-essential catalog query was refused, so the caller can say so rather than
    // silently presenting a partially informed result as complete.
    public string? DependencyWarning { get; private set; }

    public SqlServerSchemaReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var info = await SqlServerProvider.ReadServerInfoAsync(connection, cancellationToken).ConfigureAwait(false);
        var tables = new Dictionary<ObjectIdentity, TableAccumulator>();

        await ReadColumnsAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadKeyConstraintsAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadIndexesAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadForeignKeysAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadCheckConstraintsAsync(connection, tables, cancellationToken).ConfigureAwait(false);

        var dependencies = await ReadDependenciesAsync(connection, cancellationToken).ConfigureAwait(false);
        var views = await ReadViewsAsync(connection, cancellationToken).ConfigureAwait(false);
        var routines = await ReadRoutinesAsync(connection, cancellationToken).ConfigureAwait(false);

        return new DatabaseSchema
        {
            DatabaseName = info.DatabaseName,
            Collation = info.Collation,
            ReadWarnings = DependencyWarning is null ? [] : [DependencyWarning],
            Tables = tables.Values.Select(t => t.Build()).ToList(),
            Views = views.Select(v => new ViewDefinition
            {
                Identity = v.Identity,
                Definition = v.Definition,
                Extras = v.Extras,
                DependsOn = DependenciesOf(dependencies, v.Identity)
            }).ToList(),
            Routines = routines.Select(r => new RoutineDefinition
            {
                Identity = r.Identity,
                Kind = r.Kind,
                Definition = r.Definition,
                Extras = r.Extras,
                DependsOn = DependenciesOf(dependencies, r.Identity)
            }).ToList(),
            Triggers = await ReadTriggersAsync(connection, cancellationToken).ConfigureAwait(false),
            Sequences = await ReadSequencesAsync(connection, cancellationToken).ConfigureAwait(false),
            UserDefinedTypes = await ReadUserDefinedTypesAsync(connection, cancellationToken).ConfigureAwait(false)
        };
    }

    private static async Task ReadColumnsAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(connection, CatalogQueries.Columns, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = Accumulator(tables, Str(reader, "SchemaName"), Str(reader, "TableName"));

            table.Columns.Add(new ColumnDefinition
            {
                Name = Str(reader, "ColumnName"),
                OrdinalPosition = reader.GetInt32(reader.GetOrdinal("OrdinalPosition")),
                DataType = SqlTypeMapper.Map(
                    Str(reader, "TypeName"),
                    reader.GetInt16(reader.GetOrdinal("MaxLength")),
                    reader.GetByte(reader.GetOrdinal("Precision")),
                    reader.GetByte(reader.GetOrdinal("Scale")),
                    reader.GetBoolean(reader.GetOrdinal("IsUserDefined")),
                    Str(reader, "TypeSchemaName")),
                IsNullable = reader.GetBoolean(reader.GetOrdinal("IsNullable")),
                Collation = NullableStr(reader, "CollationName"),
                Identity = ReadIdentity(reader),
                ComputedExpression = NullableStr(reader, "ComputedDefinition"),
                DefaultExpression = NullableStr(reader, "DefaultDefinition"),
                DefaultConstraintName = NullableStr(reader, "DefaultConstraintName")
            });
        }
    }

    private static async Task ReadKeyConstraintsAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        var primaryKeys = new Dictionary<(ObjectIdentity Table, string Name), (bool Clustered, List<IndexColumn> Columns)>();
        var uniques = new Dictionary<(ObjectIdentity Table, string Name), (bool Clustered, List<IndexColumn> Columns)>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.KeyConstraints, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = TableId(Str(reader, "SchemaName"), Str(reader, "TableName"));
                var name = Str(reader, "ConstraintName");
                var clustered = Str(reader, "IndexType").StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase);
                var column = new IndexColumn(
                    Str(reader, "ColumnName"),
                    reader.GetBoolean(reader.GetOrdinal("IsDescending")));

                var bucket = Str(reader, "ConstraintType").Trim() == "PK" ? primaryKeys : uniques;
                var key = (identity, name);

                if (!bucket.TryGetValue(key, out var entry))
                {
                    entry = (clustered, []);
                    bucket[key] = entry;
                }

                entry.Columns.Add(column);
            }
        }

        foreach (var ((identity, name), (clustered, columns)) in primaryKeys)
        {
            Accumulator(tables, identity).PrimaryKey = new PrimaryKeyDefinition
            {
                Name = name,
                Columns = columns,
                IsClustered = clustered
            };
        }

        foreach (var ((identity, name), (clustered, columns)) in uniques)
        {
            Accumulator(tables, identity).UniqueConstraints.Add(new UniqueConstraintDefinition
            {
                Name = name,
                Columns = columns,
                IsClustered = clustered
            });
        }
    }

    private static async Task ReadIndexesAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<(ObjectIdentity Table, string Name), (bool Unique, bool Clustered, string? Filter, int FillFactor, List<IndexColumn> Key, List<string> Included)>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.Indexes, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = TableId(Str(reader, "SchemaName"), Str(reader, "TableName"));
                var key = (identity, Str(reader, "IndexName"));

                if (!indexes.TryGetValue(key, out var entry))
                {
                    entry = (
                        reader.GetBoolean(reader.GetOrdinal("IsUnique")),
                        Str(reader, "IndexType").StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase),
                        NullableStr(reader, "FilterDefinition"),
                        reader.GetByte(reader.GetOrdinal("FillFactor")),
                        [],
                        []);
                    indexes[key] = entry;
                }

                if (reader.GetBoolean(reader.GetOrdinal("IsIncluded")))
                {
                    entry.Included.Add(Str(reader, "ColumnName"));
                }
                else
                {
                    entry.Key.Add(new IndexColumn(
                        Str(reader, "ColumnName"),
                        reader.GetBoolean(reader.GetOrdinal("IsDescending"))));
                }
            }
        }

        foreach (var ((identity, name), entry) in indexes)
        {
            Accumulator(tables, identity).Indexes.Add(new IndexDefinition
            {
                Name = name,
                Columns = entry.Key,
                IncludedColumns = entry.Included,
                IsUnique = entry.Unique,
                IsClustered = entry.Clustered,
                FilterExpression = entry.Filter,
                Extras = new ProviderExtras(new Dictionary<string, string?>
                {
                    ["FillFactor"] = entry.FillFactor.ToString()
                })
            });
        }
    }

    private static async Task ReadForeignKeysAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        var keys = new Dictionary<(ObjectIdentity Table, string Name), (ObjectIdentity Referenced, ReferentialAction OnDelete, ReferentialAction OnUpdate, bool Disabled, List<string> Columns, List<string> ReferencedColumns)>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.ForeignKeys, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = TableId(Str(reader, "SchemaName"), Str(reader, "TableName"));
                var key = (identity, Str(reader, "ForeignKeyName"));

                if (!keys.TryGetValue(key, out var entry))
                {
                    entry = (
                        TableId(Str(reader, "ReferencedSchema"), Str(reader, "ReferencedTable")),
                        SqlTypeMapper.MapAction(reader.GetByte(reader.GetOrdinal("DeleteAction"))),
                        SqlTypeMapper.MapAction(reader.GetByte(reader.GetOrdinal("UpdateAction"))),
                        reader.GetBoolean(reader.GetOrdinal("IsDisabled")),
                        [],
                        []);
                    keys[key] = entry;
                }

                entry.Columns.Add(Str(reader, "ColumnName"));
                entry.ReferencedColumns.Add(Str(reader, "ReferencedColumn"));
            }
        }

        foreach (var ((identity, name), entry) in keys)
        {
            Accumulator(tables, identity).ForeignKeys.Add(new ForeignKeyDefinition
            {
                Name = name,
                Columns = entry.Columns,
                ReferencedTable = entry.Referenced,
                ReferencedColumns = entry.ReferencedColumns,
                OnDelete = entry.OnDelete,
                OnUpdate = entry.OnUpdate,
                IsDisabled = entry.Disabled
            });
        }
    }

    private static async Task ReadCheckConstraintsAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(connection, CatalogQueries.CheckConstraints, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = Accumulator(tables, Str(reader, "SchemaName"), Str(reader, "TableName"));

            table.CheckConstraints.Add(new CheckConstraintDefinition
            {
                Name = Str(reader, "ConstraintName"),
                Expression = Str(reader, "Definition"),
                IsDisabled = reader.GetBoolean(reader.GetOrdinal("IsDisabled"))
            });
        }
    }

    private static async Task<List<ViewDefinition>> ReadViewsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var views = new List<ViewDefinition>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.Views, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            views.Add(new ViewDefinition
            {
                Identity = new ObjectIdentity(ObjectType.View, Str(reader, "SchemaName"), Str(reader, "ViewName")),
                Definition = Str(reader, "Definition")
            });
        }

        return views;
    }

    private static async Task<List<RoutineDefinition>> ReadRoutinesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var routines = new List<RoutineDefinition>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.Routines, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            routines.Add(new RoutineDefinition
            {
                Identity = new ObjectIdentity(ObjectType.Routine, Str(reader, "SchemaName"), Str(reader, "RoutineName")),
                Kind = SqlTypeMapper.MapRoutineKind(Str(reader, "RoutineType")),
                Definition = Str(reader, "Definition")
            });
        }

        return routines;
    }

    private static async Task<List<TriggerDefinition>> ReadTriggersAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var triggers = new List<TriggerDefinition>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.Triggers, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = Str(reader, "SchemaName");

            triggers.Add(new TriggerDefinition
            {
                Identity = new ObjectIdentity(ObjectType.Trigger, schema, Str(reader, "TriggerName")),
                Table = TableId(schema, Str(reader, "TableName")),
                Definition = Str(reader, "Definition"),
                IsDisabled = reader.GetBoolean(reader.GetOrdinal("IsDisabled"))
            });
        }

        return triggers;
    }

    // Two queries because the catalog keeps them apart: sys.types holds alias and CLR types, sys.table_types
    // holds table types and their columns live in sys.columns like any other table's.
    private static async Task<List<UserDefinedTypeDefinition>> ReadUserDefinedTypesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var types = new List<UserDefinedTypeDefinition>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.ScalarTypes, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var assembly = reader.GetBoolean(reader.GetOrdinal("IsAssemblyType"));
                var baseType = NullableStr(reader, "BaseTypeName");

                types.Add(new UserDefinedTypeDefinition
                {
                    Identity = new ObjectIdentity(
                        ObjectType.UserDefinedType, Str(reader, "SchemaName"), Str(reader, "TypeName")),
                    Kind = assembly ? UserDefinedTypeKind.Clr : UserDefinedTypeKind.Alias,
                    BaseType = assembly || baseType is null
                        ? null
                        : SqlTypeMapper.Map(
                            baseType,
                            reader.GetInt16(reader.GetOrdinal("MaxLength")),
                            reader.GetByte(reader.GetOrdinal("Precision")),
                            reader.GetByte(reader.GetOrdinal("Scale"))),
                    IsNullable = reader.GetBoolean(reader.GetOrdinal("IsNullable"))
                });
            }
        }

        var tableTypes = new Dictionary<ObjectIdentity, List<ColumnDefinition>>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.TableTypes, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = new ObjectIdentity(
                    ObjectType.UserDefinedType, Str(reader, "SchemaName"), Str(reader, "TypeName"));

                if (!tableTypes.TryGetValue(identity, out var columns))
                {
                    columns = [];
                    tableTypes[identity] = columns;
                }

                columns.Add(new ColumnDefinition
                {
                    Name = Str(reader, "ColumnName"),
                    OrdinalPosition = reader.GetInt32(reader.GetOrdinal("OrdinalPosition")),
                    DataType = SqlTypeMapper.Map(
                        Str(reader, "ColumnTypeName"),
                        reader.GetInt16(reader.GetOrdinal("MaxLength")),
                        reader.GetByte(reader.GetOrdinal("Precision")),
                        reader.GetByte(reader.GetOrdinal("Scale")),
                        reader.GetBoolean(reader.GetOrdinal("IsUserDefined")),
                        Str(reader, "TypeSchemaName")),
                    IsNullable = reader.GetBoolean(reader.GetOrdinal("IsNullable"))
                });
            }
        }

        types.AddRange(tableTypes.Select(pair => new UserDefinedTypeDefinition
        {
            Identity = pair.Key,
            Kind = UserDefinedTypeKind.Table,
            Columns = pair.Value.OrderBy(c => c.OrdinalPosition).ToList()
        }));

        return types.OrderBy(t => t.Identity.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<SequenceDefinition>> ReadSequencesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var sequences = new List<SequenceDefinition>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.Sequences, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sequences.Add(new SequenceDefinition
            {
                Identity = new ObjectIdentity(ObjectType.Sequence, Str(reader, "SchemaName"), Str(reader, "SequenceName")),
                DataType = SqlTypeMapper.Map(
                    Str(reader, "TypeName"),
                    0,
                    reader.GetByte(reader.GetOrdinal("Precision")),
                    reader.GetByte(reader.GetOrdinal("Scale"))),
                StartValue = reader.GetInt64(reader.GetOrdinal("StartValue")),
                Increment = reader.GetInt64(reader.GetOrdinal("Increment")),
                MinValue = reader.GetInt64(reader.GetOrdinal("MinimumValue")),
                MaxValue = reader.GetInt64(reader.GetOrdinal("MaximumValue")),
                IsCycling = reader.GetBoolean(reader.GetOrdinal("IsCycling"))
            });
        }

        return sequences;
    }

    // Keyed on schema-qualified name rather than ObjectType, because the same body can reference a
    // view and a procedure and the emitter only cares that one comes before the other.
    // Reading this view needs VIEW DEFINITION, which a read-only account on a shared server often
    // does not have. It only improves emission order, so losing it must not take the whole schema read
    // down with it: the compare still works, and the caller is told the ordering is unverified.
    private async Task<ILookup<string, ObjectIdentity>> ReadDependenciesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var pairs = new List<(string Referencing, ObjectIdentity Referenced)>();

        try
        {
            await using var reader = await ExecuteAsync(connection, CatalogQueries.ProgrammableDependencies, cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var referencing = $"{Str(reader, "SchemaName")}.{Str(reader, "Name")}";
                var type = Str(reader, "ReferencedType").Trim() switch
                {
                    "U" => ObjectType.Table,
                    "V" => ObjectType.View,
                    _ => ObjectType.Routine
                };

                pairs.Add((
                    referencing,
                    new ObjectIdentity(type, Str(reader, "ReferencedSchema"), Str(reader, "ReferencedName"))));
            }
        }
        catch (SqlException ex) when (ex.Number is PermissionDenied or ObjectNotFound)
        {
            DependencyWarning =
                "Object dependencies could not be read (VIEW DEFINITION permission is missing), so views and "
                + "procedures are emitted in name order. Check that order by hand if one of them selects from another.";
        }

        return pairs.ToLookup(p => p.Referencing, p => p.Referenced, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ObjectIdentity> DependenciesOf(
        ILookup<string, ObjectIdentity> dependencies,
        ObjectIdentity identity) =>
        dependencies[identity.QualifiedName].ToList();

    private static IdentitySpec? ReadIdentity(SqlDataReader reader)
    {
        var seed = reader.GetOrdinal("SeedValue");
        var increment = reader.GetOrdinal("IncrementValue");

        if (reader.IsDBNull(seed) || reader.IsDBNull(increment))
        {
            return null;
        }

        // Both are sql_variant, so the CLR type follows the column's own type rather than being bigint.
        return new IdentitySpec(
            Convert.ToInt64(reader.GetValue(seed), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(increment), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static Task<SqlDataReader> ExecuteAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = new SqlCommand(sql, connection);
        return command.ExecuteReaderAsync(cancellationToken);
    }

    private static TableAccumulator Accumulator(
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        string schema,
        string name) =>
        Accumulator(tables, TableId(schema, name));

    private static TableAccumulator Accumulator(
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        ObjectIdentity identity)
    {
        if (!tables.TryGetValue(identity, out var accumulator))
        {
            accumulator = new TableAccumulator(identity);
            tables[identity] = accumulator;
        }

        return accumulator;
    }

    private static ObjectIdentity TableId(string schema, string name) =>
        new(ObjectType.Table, schema, name);

    private static string Str(SqlDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    private static string? NullableStr(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
