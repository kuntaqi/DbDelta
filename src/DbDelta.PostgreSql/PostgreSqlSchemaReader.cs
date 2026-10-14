using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using Npgsql;

namespace DbDelta.PostgreSql;

// First increment: tables, columns, primary keys, unique constraints, foreign keys, indexes and check
// constraints — the structural heart a schema compare turns on. Views, routines, triggers, sequences and
// user-defined types are not read yet and are surfaced through ReadWarnings rather than left to look absent.
// See docs/TASK-postgresql-schema-reader.md for the deferred part and the cross-engine caveats.
public sealed class PostgreSqlSchemaReader : ISchemaReader
{
    private const string InsufficientPrivilege = "42501";

    private const string DeferredObjectsWarning =
        "Sequences and user-defined types are not read by the PostgreSQL provider yet; this schema reader "
        + "covers tables, views, routines and triggers but not those two object kinds.";

    private readonly string _connectionString;

    public PostgreSqlSchemaReader(string connectionString) => _connectionString = connectionString;

    public async Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var (databaseName, collation) = await ReadDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        var tables = new Dictionary<ObjectIdentity, PgTableParts>();

        await ReadTablesAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await ReadColumnsAsync(connection, tables, warnings, cancellationToken).ConfigureAwait(false);
        await ReadConstraintsAsync(connection, tables, warnings, cancellationToken).ConfigureAwait(false);
        await ReadIndexesAsync(connection, tables, warnings, cancellationToken).ConfigureAwait(false);

        var views = await ReadViewsAsync(connection, warnings, cancellationToken).ConfigureAwait(false);
        var routines = await ReadRoutinesAsync(connection, warnings, cancellationToken).ConfigureAwait(false);
        var triggers = await ReadTriggersAsync(connection, warnings, cancellationToken).ConfigureAwait(false);

        warnings.Add(DeferredObjectsWarning);

        var built = tables.Values
            .OrderBy(t => t.Identity.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Identity.Name, StringComparer.Ordinal)
            .Select(t => t.Build())
            .ToList();

        return new DatabaseSchema
        {
            DatabaseName = databaseName,
            Collation = collation,
            Tables = built,
            Views = views,
            Routines = routines,
            Triggers = triggers,
            ReadWarnings = warnings,
        };
    }

    private async Task<(string Name, string? Collation)> ReadDatabaseAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PgCatalogQueries.Database, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The database did not return its own name.");
        }

        return (Str(reader, "name"), NStr(reader, "collation"));
    }

    private async Task ReadTablesAsync(
        NpgsqlConnection connection, Dictionary<ObjectIdentity, PgTableParts> tables, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PgCatalogQueries.Tables, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var identity = new ObjectIdentity(ObjectType.Table, Str(reader, "schema_name"), Str(reader, "table_name"));
            tables[identity] = new PgTableParts(identity);
        }
    }

    private async Task ReadColumnsAsync(
        NpgsqlConnection connection, Dictionary<ObjectIdentity, PgTableParts> tables,
        List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(PgCatalogQueries.Columns, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!TryTable(tables, reader, out var parts))
                {
                    continue;
                }

                var typeKind = Str(reader, "type_kind");
                var isUserDefined = typeKind is "d" or "e" or "c" or "r" or "m";
                var dataType = isUserDefined
                    ? PgTypeMapper.Map(Str(reader, "type_name_raw"), null, null, null, null, true, NStr(reader, "type_schema"))
                    : PgTypeMapper.Map(
                        Str(reader, "type_name"),
                        NInt(reader, "char_len"),
                        NInt(reader, "num_prec"),
                        NInt(reader, "num_scale"),
                        NInt(reader, "dt_prec"));

                var generated = NStr(reader, "generated_kind");
                var isStored = generated == "s";
                var defaultExpr = NStr(reader, "default_expr");
                var identityKind = NStr(reader, "identity_kind");
                var seedStart = NLong(reader, "seq_start");
                var seedIncrement = NLong(reader, "seq_inc");

                parts.Columns.Add(new ColumnDefinition
                {
                    Name = Str(reader, "column_name"),
                    DataType = dataType,
                    IsNullable = Bool(reader, "is_nullable"),
                    OrdinalPosition = Int(reader, "ordinal"),
                    Collation = NStr(reader, "collation_name"),
                    Identity = identityKind is "a" or "d" && seedStart is long s && seedIncrement is long inc
                        ? new IdentitySpec(s, inc)
                        : null,
                    ComputedExpression = isStored ? defaultExpr : null,
                    DefaultExpression = isStored ? null : defaultExpr,
                });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            warnings.Add("Columns could not be read in full: " + ex.MessageText);
        }
    }

    private async Task ReadConstraintsAsync(
        NpgsqlConnection connection, Dictionary<ObjectIdentity, PgTableParts> tables,
        List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(PgCatalogQueries.Constraints, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!TryTable(tables, reader, out var parts))
                {
                    continue;
                }

                var name = Str(reader, "name");
                switch (Str(reader, "kind"))
                {
                    case "p":
                        parts.PrimaryKey = new PrimaryKeyDefinition
                        {
                            Name = name,
                            Columns = KeyColumns(reader),
                            IsClustered = false,
                        };
                        break;
                    case "u":
                        parts.UniqueConstraints.Add(new UniqueConstraintDefinition
                        {
                            Name = name,
                            Columns = KeyColumns(reader),
                            IsClustered = false,
                        });
                        break;
                    case "f":
                        parts.ForeignKeys.Add(new ForeignKeyDefinition
                        {
                            Name = name,
                            Columns = NArr(reader, "cols") ?? [],
                            ReferencedTable = new ObjectIdentity(
                                ObjectType.Table, Str(reader, "ref_schema"), Str(reader, "ref_table")),
                            ReferencedColumns = NArr(reader, "ref_cols") ?? [],
                            OnDelete = PgTypeMapper.MapReferentialAction(Str(reader, "on_delete")),
                            OnUpdate = PgTypeMapper.MapReferentialAction(Str(reader, "on_update")),
                            IsDisabled = !Bool(reader, "validated"),
                        });
                        break;
                    case "c":
                        parts.CheckConstraints.Add(new CheckConstraintDefinition
                        {
                            Name = name,
                            Expression = NStr(reader, "check_expr") ?? string.Empty,
                            IsDisabled = !Bool(reader, "validated"),
                        });
                        break;
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            warnings.Add("Constraints could not be read in full: " + ex.MessageText);
        }
    }

    private async Task ReadIndexesAsync(
        NpgsqlConnection connection, Dictionary<ObjectIdentity, PgTableParts> tables,
        List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var accumulators = new Dictionary<string, PgIndexAccumulator>(StringComparer.Ordinal);

            await using (var command = new NpgsqlCommand(PgCatalogQueries.Indexes, connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var schema = Str(reader, "schema_name");
                    var table = Str(reader, "table_name");
                    var name = Str(reader, "index_name");
                    accumulators[Key(schema, table, name)] = new PgIndexAccumulator
                    {
                        Table = new ObjectIdentity(ObjectType.Table, schema, table),
                        Name = name,
                        IsUnique = Bool(reader, "is_unique"),
                        Kind = PgTypeMapper.MapIndexKind(Str(reader, "method")),
                        FilterExpression = NStr(reader, "predicate"),
                        FillFactor = FillFactor(NArr(reader, "reloptions")),
                    };
                }
            }

            await using (var command = new NpgsqlCommand(PgCatalogQueries.IndexColumns, connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var key = Key(Str(reader, "schema_name"), Str(reader, "table_name"), Str(reader, "index_name"));
                    if (!accumulators.TryGetValue(key, out var accumulator))
                    {
                        continue;
                    }

                    var column = Str(reader, "column_name");
                    if (Bool(reader, "is_included"))
                    {
                        accumulator.IncludedColumns.Add(column);
                    }
                    else
                    {
                        accumulator.KeyColumns.Add(new IndexColumn(column, Bool(reader, "is_descending")));
                    }
                }
            }

            foreach (var accumulator in accumulators.Values)
            {
                if (tables.TryGetValue(accumulator.Table, out var parts))
                {
                    parts.Indexes.Add(accumulator.Build());
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            warnings.Add("Indexes could not be read in full: " + ex.MessageText);
        }
    }

    private async Task<List<ViewDefinition>> ReadViewsAsync(
        NpgsqlConnection connection, List<string> warnings, CancellationToken cancellationToken)
    {
        var views = new List<ViewDefinition>();

        try
        {
            await using var command = new NpgsqlCommand(PgCatalogQueries.Views, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                views.Add(new ViewDefinition
                {
                    Identity = new ObjectIdentity(
                        ObjectType.View, Str(reader, "schema_name"), Str(reader, "view_name")),
                    Definition = Str(reader, "definition"),
                });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            warnings.Add("Views could not be read in full: " + ex.MessageText);
        }

        return views;
    }

    private async Task<List<RoutineDefinition>> ReadRoutinesAsync(
        NpgsqlConnection connection, List<string> warnings, CancellationToken cancellationToken)
    {
        var routines = new List<RoutineDefinition>();

        try
        {
            await using var command = new NpgsqlCommand(PgCatalogQueries.Routines, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                routines.Add(new RoutineDefinition
                {
                    Identity = new ObjectIdentity(
                        ObjectType.Routine, Str(reader, "schema_name"), Str(reader, "routine_name")),
                    Kind = PgTypeMapper.MapRoutineKind(Str(reader, "kind"), Bool(reader, "returns_set")),
                    Definition = Str(reader, "definition"),
                });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            warnings.Add("Routines could not be read in full: " + ex.MessageText);
        }

        return routines;
    }

    private async Task<List<TriggerDefinition>> ReadTriggersAsync(
        NpgsqlConnection connection, List<string> warnings, CancellationToken cancellationToken)
    {
        var triggers = new List<TriggerDefinition>();

        try
        {
            await using var command = new NpgsqlCommand(PgCatalogQueries.Triggers, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = Str(reader, "schema_name");
                var table = Str(reader, "table_name");
                var name = Str(reader, "trigger_name");

                triggers.Add(new TriggerDefinition
                {
                    // A PostgreSQL trigger name is unique per table, not per schema, so the table qualifies it.
                    Identity = new ObjectIdentity(ObjectType.Trigger, schema, $"{table}.{name}"),
                    Table = new ObjectIdentity(ObjectType.Table, schema, table),
                    Definition = Str(reader, "definition"),
                    IsDisabled = Str(reader, "enabled") == "D",
                });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            warnings.Add("Triggers could not be read in full: " + ex.MessageText);
        }

        return triggers;
    }

    private static bool TryTable(
        Dictionary<ObjectIdentity, PgTableParts> tables, NpgsqlDataReader reader, out PgTableParts parts)
    {
        var identity = new ObjectIdentity(ObjectType.Table, Str(reader, "schema_name"), Str(reader, "table_name"));
        return tables.TryGetValue(identity, out parts!);
    }

    private static IReadOnlyList<IndexColumn> KeyColumns(NpgsqlDataReader reader) =>
        (NArr(reader, "cols") ?? []).Select(c => new IndexColumn(c)).ToList();

    private static string FillFactor(string[]? reloptions)
    {
        if (reloptions is not null)
        {
            foreach (var option in reloptions)
            {
                if (option.StartsWith("fillfactor=", StringComparison.OrdinalIgnoreCase))
                {
                    return option["fillfactor=".Length..];
                }
            }
        }

        return "100";
    }

    private static string Key(string schema, string table, string index) =>
        string.Join('\u0001', schema, table, index);

    private static string Str(NpgsqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    private static string? NStr(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int Int(NpgsqlDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    private static int? NInt(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? NLong(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static bool Bool(NpgsqlDataReader reader, string column) => reader.GetBoolean(reader.GetOrdinal(column));

    private static string[]? NArr(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string[]>(ordinal);
    }
}
