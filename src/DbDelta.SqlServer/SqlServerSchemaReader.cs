using System.Globalization;
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

    public string? ColumnReferenceWarning { get; private set; }

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

        var references = await ReadColumnReferencesAsync(connection, cancellationToken).ConfigureAwait(false);

        await ReadColumnsAsync(connection, tables, references, cancellationToken).ConfigureAwait(false);
        await ReadKeyConstraintsAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadIndexesAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadForeignKeysAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadCheckConstraintsAsync(connection, tables, references, cancellationToken).ConfigureAwait(false);
        await ReadStatisticsAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadTableStorageAsync(connection, tables, cancellationToken).ConfigureAwait(false);

        // Only onto tables already known: a reference to a table with no columns read cannot exist.
        foreach (var (table, reference) in references.SchemaBound())
        {
            if (tables.TryGetValue(table, out var accumulator))
            {
                accumulator.SchemaBoundReferences.Add(reference);
            }
        }

        var dependencies = await ReadDependenciesAsync(connection, cancellationToken).ConfigureAwait(false);
        var views = await ReadViewsAsync(connection, cancellationToken).ConfigureAwait(false);
        var routines = await ReadRoutinesAsync(connection, cancellationToken).ConfigureAwait(false);

        return new DatabaseSchema
        {
            DatabaseName = info.DatabaseName,
            Collation = info.Collation,
            ReadWarnings = new[] { DependencyWarning, ColumnReferenceWarning }.OfType<string>().ToList(),
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

    private async Task<ColumnReferenceMap> ReadColumnReferencesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var map = new ColumnReferenceMap();

        try
        {
            await using var reader = await ExecuteAsync(connection, CatalogQueries.ColumnReferences, cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = TableId(Str(reader, "SchemaName"), Str(reader, "TableName"));
                var column = NullableStr(reader, "ColumnName");
                var name = Str(reader, "ReferencingName");

                switch (Str(reader, "ReferencingType").Trim())
                {
                    case "C" when column is not null:
                        map.AddCheck(table, name, column);
                        break;

                    case "U" when column is not null && NullableStr(reader, "ReferencingColumn") is { } computed:
                        map.AddComputed(table, computed, column);
                        break;

                    case "V":
                        map.AddModule(table, new ObjectIdentity(ObjectType.View, Str(reader, "ReferencingSchema"), name), column);
                        break;

                    case "FN" or "IF" or "TF":
                        map.AddModule(table, new ObjectIdentity(ObjectType.Routine, Str(reader, "ReferencingSchema"), name), column);
                        break;

                    default:
                        break;
                }
            }
        }
        catch (SqlException ex) when (ex.Number is PermissionDenied or ObjectNotFound)
        {
            ColumnReferenceWarning =
                "Column dependencies could not be read (VIEW DEFINITION permission is missing), so a column change "
                + "may be emitted without dropping the check constraints, computed columns or schema-bound views "
                + "that hold the column. The server will refuse such a change and the script will roll back.";
        }

        return map;
    }

    private static async Task ReadStatisticsAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        var statistics = new Dictionary<(ObjectIdentity Table, string Name), (string? Filter, bool NoRecompute, List<string> Columns)>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.Statistics, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (TableId(Str(reader, "SchemaName"), Str(reader, "TableName")), Str(reader, "StatisticsName"));

                if (!statistics.TryGetValue(key, out var entry))
                {
                    entry = (
                        NullableStr(reader, "FilterDefinition"),
                        reader.GetBoolean(reader.GetOrdinal("NoRecompute")),
                        []);
                    statistics[key] = entry;
                }

                entry.Columns.Add(Str(reader, "ColumnName"));
            }
        }

        foreach (var ((identity, name), entry) in statistics)
        {
            if (tables.TryGetValue(identity, out var table))
            {
                table.Statistics.Add(new StatisticsDefinition
                {
                    Name = name,
                    Columns = entry.Columns,
                    FilterExpression = entry.Filter,
                    NoRecompute = entry.NoRecompute
                });
            }
        }
    }

    private static async Task ReadTableStorageAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(connection, CatalogQueries.TableStorage, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!tables.TryGetValue(TableId(Str(reader, "SchemaName"), Str(reader, "TableName")), out var table))
            {
                continue;
            }

            var blockers = new List<string>();

            if (Int(reader, "TemporalType") is > 0)
            {
                blockers.Add("it is part of a system-versioned temporal pair");
            }

            if (Int(reader, "MemoryOptimized") is > 0)
            {
                blockers.Add("it is memory-optimized");
            }

            if (reader.GetBoolean(reader.GetOrdinal("IsPartitioned")))
            {
                blockers.Add("it is partitioned, and the rebuilt table would not be");
            }

            if (reader.GetBoolean(reader.GetOrdinal("IsReplicated")))
            {
                blockers.Add("it is published for replication");
            }

            if (reader.GetBoolean(reader.GetOrdinal("IsTrackedByCdc")))
            {
                blockers.Add("change data capture is tracking it");
            }

            if (blockers.Count > 0)
            {
                table.Extras[TableExtras.RebuildBlockers] = string.Join("; ", blockers);
            }
        }
    }

    private static async Task ReadColumnsAsync(
        SqlConnection connection,
        Dictionary<ObjectIdentity, TableAccumulator> tables,
        ColumnReferenceMap references,
        CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(connection, CatalogQueries.Columns, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = Accumulator(tables, Str(reader, "SchemaName"), Str(reader, "TableName"));
            var name = Str(reader, "ColumnName");

            table.Columns.Add(new ColumnDefinition
            {
                Name = name,
                ComputedFrom = references.ComputedFrom(table.Identity, name),
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
        var indexes = new Dictionary<(ObjectIdentity Table, string Name), (IndexKind Kind, bool Unique, bool Clustered, string? Filter, int FillFactor, List<IndexColumn> Key, List<string> Included)>();

        var spatial = await ReadSpatialTessellationsAsync(connection, cancellationToken).ConfigureAwait(false);
        var xml = await ReadXmlIndexShapesAsync(connection, cancellationToken).ConfigureAwait(false);
        var order = await ReadColumnstoreOrderAsync(connection, cancellationToken).ConfigureAwait(false);

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.Indexes, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = TableId(Str(reader, "SchemaName"), Str(reader, "TableName"));
                var key = (identity, Str(reader, "IndexName"));
                var type = Str(reader, "IndexType");

                if (!indexes.TryGetValue(key, out var entry))
                {
                    entry = (
                        KindOf(type),
                        reader.GetBoolean(reader.GetOrdinal("IsUnique")),
                        type.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase),
                        NullableStr(reader, "FilterDefinition"),
                        reader.GetByte(reader.GetOrdinal("FillFactor")),
                        [],
                        []);
                    indexes[key] = entry;
                }

                // A columnstore reports every one of its columns as an included column with key ordinal
                // zero — measured, not assumed — so the reader used to give it no key columns at all and
                // the emitter wrote CREATE INDEX with an empty column list. For a nonclustered columnstore
                // that list is what the DDL states, so it belongs in Columns.
                //
                // A clustered columnstore is different again: it covers the whole table, so its DDL names
                // no columns at all — except the ORDER columns of an ordered one, which are the only
                // columns it does state. Those are filled in below from the one query that can tell them
                // apart. Its own column rows are dropped either way, which also stops adding a column to
                // the table from looking like a change to the index.
                if (entry.Kind == IndexKind.Columnstore)
                {
                    if (!entry.Clustered)
                    {
                        entry.Key.Add(new IndexColumn(Str(reader, "ColumnName"), false));
                    }

                    continue;
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
            var extras = new Dictionary<string, string?>
            {
                ["FillFactor"] = entry.FillFactor.ToString()
            };

            if (spatial.TryGetValue((identity, name), out var tessellation))
            {
                foreach (var (k, v) in tessellation)
                {
                    extras[k] = v;
                }
            }

            if (xml.TryGetValue((identity, name), out var shape))
            {
                foreach (var (k, v) in shape)
                {
                    extras[k] = v;
                }
            }

            var columns = entry.Kind == IndexKind.Columnstore && entry.Clustered
                && order.TryGetValue((identity, name), out var ordered)
                    ? ordered
                    : entry.Key;

            Accumulator(tables, identity).Indexes.Add(new IndexDefinition
            {
                Name = name,
                Kind = entry.Kind,
                Columns = columns,
                IncludedColumns = entry.Included,
                IsUnique = entry.Unique,
                IsClustered = entry.Clustered,
                FilterExpression = entry.Filter,
                Extras = new ProviderExtras(extras)
            });
        }
    }

    // sys.indexes.type_desc, which used to be collapsed to the single boolean IsClustered here. HEAP never
    // arrives (the query filters type 0) and an unrecognised value is left as rowstore rather than guessed
    // at — the emitter is what refuses a kind it cannot write, and it needs to see a kind to do that.
    private static IndexKind KindOf(string typeDescription) => typeDescription.ToUpperInvariant() switch
    {
        "XML" => IndexKind.Xml,
        "SPATIAL" => IndexKind.Spatial,
        "CLUSTERED COLUMNSTORE" or "NONCLUSTERED COLUMNSTORE" => IndexKind.Columnstore,
        "NONCLUSTERED HASH" => IndexKind.Hash,
        _ => IndexKind.Rowstore
    };

    private static async Task<Dictionary<(ObjectIdentity Table, string Name), List<IndexColumn>>>
        ReadColumnstoreOrderAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(ObjectIdentity, string), List<IndexColumn>>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.ColumnstoreOrder, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (TableId(Str(reader, "SchemaName"), Str(reader, "TableName")), Str(reader, "IndexName"));

            if (!result.TryGetValue(key, out var columns))
            {
                columns = [];
                result[key] = columns;
            }

            columns.Add(new IndexColumn(Str(reader, "ColumnName"), false));
        }

        return result;
    }

    private static async Task<Dictionary<(ObjectIdentity Table, string Name), Dictionary<string, string?>>>
        ReadSpatialTessellationsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(ObjectIdentity, string), Dictionary<string, string?>>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.SpatialIndexes, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, string?>
            {
                [SpatialExtras.Scheme] = Str(reader, "Scheme")
            };

            // The bounding box is mandatory DDL for a GEOMETRY_GRID and absent for every other scheme, so
            // it is carried only when all four bounds are there rather than defaulted to something.
            var bounds = new[] { "XMin", "YMin", "XMax", "YMax" }
                .Select(c => Double(reader, c))
                .ToList();

            if (bounds.All(b => b is not null))
            {
                values[SpatialExtras.BoundingBox] = string.Join(", ", bounds.Select(Number));
            }

            var grids = new[] { "Level1", "Level2", "Level3", "Level4" }
                .Select(c => NullableStr(reader, c))
                .ToList();

            if (grids.All(g => g is not null))
            {
                values[SpatialExtras.Grids] = string.Join(", ", grids);
            }

            if (Int(reader, "CellsPerObject") is { } cells)
            {
                values[SpatialExtras.CellsPerObject] = cells.ToString(CultureInfo.InvariantCulture);
            }

            result[(TableId(Str(reader, "SchemaName"), Str(reader, "TableName")), Str(reader, "IndexName"))] = values;
        }

        return result;
    }

    private static async Task<Dictionary<(ObjectIdentity Table, string Name), Dictionary<string, string?>>>
        ReadXmlIndexShapesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(ObjectIdentity, string), Dictionary<string, string?>>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.XmlIndexes, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[(TableId(Str(reader, "SchemaName"), Str(reader, "TableName")), Str(reader, "IndexName"))] =
                new Dictionary<string, string?>
                {
                    [XmlExtras.Kind] = NullableStr(reader, "XmlKind"),
                    [XmlExtras.SecondaryType] = NullableStr(reader, "SecondaryType"),
                    [XmlExtras.PrimaryIndex] = NullableStr(reader, "PrimaryIndexName")
                };
        }

        return result;
    }

    private static double? Double(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static int? Int(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static string Number(double? value) =>
        value!.Value.ToString("0.################", CultureInfo.InvariantCulture);

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
        ColumnReferenceMap references,
        CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(connection, CatalogQueries.CheckConstraints, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = Accumulator(tables, Str(reader, "SchemaName"), Str(reader, "TableName"));
            var name = Str(reader, "ConstraintName");

            table.CheckConstraints.Add(new CheckConstraintDefinition
            {
                Name = name,
                Expression = Str(reader, "Definition"),
                IsDisabled = reader.GetBoolean(reader.GetOrdinal("IsDisabled")),
                Columns = references.CheckColumns(table.Identity, name)
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
                    IsNullable = reader.GetBoolean(reader.GetOrdinal("IsNullable")),
                    DefaultExpression = NullableStr(reader, "DefaultDefinition"),
                    ComputedExpression = NullableStr(reader, "ComputedDefinition")
                });
            }
        }

        var keys = await ReadTableTypeKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var checks = await ReadTableTypeChecksAsync(connection, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadTableTypeIndexesAsync(connection, cancellationToken).ConfigureAwait(false);

        types.AddRange(tableTypes.Select(pair =>
        {
            keys.TryGetValue(pair.Key, out var key);

            return new UserDefinedTypeDefinition
            {
                Identity = pair.Key,
                Kind = UserDefinedTypeKind.Table,
                Columns = pair.Value.OrderBy(c => c.OrdinalPosition).ToList(),
                PrimaryKey = key.PrimaryKey,
                UniqueConstraints = key.Uniques ?? [],
                CheckConstraints = checks.TryGetValue(pair.Key, out var check) ? check : [],
                Indexes = indexes.TryGetValue(pair.Key, out var index) ? index : []
            };
        }));

        return types.OrderBy(t => t.Identity.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
    }

// Keyed on the type rather than the backing object, so the caller never has to know that a table type
    // has one.
    private static async Task<Dictionary<ObjectIdentity, (PrimaryKeyDefinition? PrimaryKey, List<UniqueConstraintDefinition>? Uniques)>>
        ReadTableTypeKeysAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var primaries = new Dictionary<ObjectIdentity, (bool Clustered, List<IndexColumn> Columns)>();
        var uniques = new Dictionary<(ObjectIdentity Type, string Name), (bool Clustered, List<IndexColumn> Columns)>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.TableTypeKeys, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = TypeId(Str(reader, "SchemaName"), Str(reader, "TypeName"));
                var clustered = Str(reader, "IndexType").StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase);
                var column = new IndexColumn(
                    Str(reader, "ColumnName"),
                    reader.GetBoolean(reader.GetOrdinal("IsDescending")));

                if (Str(reader, "ConstraintType").Trim() == "PK")
                {
                    if (!primaries.TryGetValue(identity, out var pk))
                    {
                        pk = (clustered, []);
                        primaries[identity] = pk;
                    }

                    pk.Columns.Add(column);
                    continue;
                }

                var key = (identity, Str(reader, "ConstraintName"));
                if (!uniques.TryGetValue(key, out var uq))
                {
                    uq = (clustered, []);
                    uniques[key] = uq;
                }

                uq.Columns.Add(column);
            }
        }

        var result = new Dictionary<ObjectIdentity, (PrimaryKeyDefinition?, List<UniqueConstraintDefinition>?)>();

        foreach (var (identity, (clustered, columns)) in primaries)
        {
            result[identity] = (
                new PrimaryKeyDefinition { Name = string.Empty, Columns = columns, IsClustered = clustered },
                result.TryGetValue(identity, out var existing) ? existing.Item2 : null);
        }

        foreach (var ((identity, name), (clustered, columns)) in uniques)
        {
            result.TryGetValue(identity, out var existing);
            var list = existing.Item2 ?? [];

            list.Add(new UniqueConstraintDefinition { Name = name, Columns = columns, IsClustered = clustered });
            result[identity] = (existing.Item1, list);
        }

        return result;
    }

    private static async Task<Dictionary<ObjectIdentity, List<CheckConstraintDefinition>>> ReadTableTypeChecksAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var checks = new Dictionary<ObjectIdentity, List<CheckConstraintDefinition>>();

        await using var reader = await ExecuteAsync(connection, CatalogQueries.TableTypeChecks, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var identity = TypeId(Str(reader, "SchemaName"), Str(reader, "TypeName"));

            if (!checks.TryGetValue(identity, out var list))
            {
                list = [];
                checks[identity] = list;
            }

            list.Add(new CheckConstraintDefinition
            {
                Name = Str(reader, "ConstraintName"),
                Expression = Str(reader, "Definition")
            });
        }

        return checks;
    }

    private static async Task<Dictionary<ObjectIdentity, List<IndexDefinition>>> ReadTableTypeIndexesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var gathered = new Dictionary<(ObjectIdentity Type, string Name), (bool Unique, bool Clustered, List<IndexColumn> Key, List<string> Included)>();

        await using (var reader = await ExecuteAsync(connection, CatalogQueries.TableTypeIndexes, cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = TypeId(Str(reader, "SchemaName"), Str(reader, "TypeName"));
                var key = (identity, Str(reader, "IndexName"));

                if (!gathered.TryGetValue(key, out var entry))
                {
                    entry = (
                        reader.GetBoolean(reader.GetOrdinal("IsUnique")),
                        Str(reader, "IndexType").StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase),
                        [],
                        []);

                    gathered[key] = entry;
                }

                if (reader.GetBoolean(reader.GetOrdinal("IsIncluded")))
                {
                    entry.Included.Add(Str(reader, "ColumnName"));
                    continue;
                }

                entry.Key.Add(new IndexColumn(
                    Str(reader, "ColumnName"),
                    reader.GetBoolean(reader.GetOrdinal("IsDescending"))));
            }
        }

        var indexes = new Dictionary<ObjectIdentity, List<IndexDefinition>>();

        foreach (var ((identity, name), (unique, clustered, keyColumns, included)) in gathered)
        {
            if (!indexes.TryGetValue(identity, out var list))
            {
                list = [];
                indexes[identity] = list;
            }

            list.Add(new IndexDefinition
            {
                Name = name,
                Columns = keyColumns,
                IncludedColumns = included,
                IsUnique = unique,
                IsClustered = clustered
            });
        }

        return indexes;
    }

    private static ObjectIdentity TypeId(string schema, string name) =>
        new(ObjectType.UserDefinedType, schema, name);

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
                // TY is this query's own code for a type, not one of SQL Server's: types are reported in a
                // separate id space and come from the second branch, so they have no sys.objects type
                // letter to carry.
                var type = Str(reader, "ReferencedType").Trim() switch
                {
                    "U" => ObjectType.Table,
                    "V" => ObjectType.View,
                    "TY" => ObjectType.UserDefinedType,
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
