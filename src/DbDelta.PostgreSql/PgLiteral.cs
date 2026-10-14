using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

// A value read as text (every reader renders with ::text) becomes a typed literal by quoting it and casting it
// back with ::type. One shape covers every type — '10.00'::numeric, '\x41'::bytea, '2024-…'::timestamptz all
// round-trip — so there is no per-type literal logic the way T-SQL needs. Only for addressing rows by value;
// the bulk data path moves rows through a staging table, not literals.
internal static class PgLiteral
{
    public static string For(DataTypeSpec type, string? value) =>
        value is null
            ? "NULL"
            : $"'{value.Replace("'", "''", System.StringComparison.Ordinal)}'::{PgTypeText.Declare(type)}";
}
