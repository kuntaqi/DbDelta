using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

// Renders a DataTypeSpec back to PostgreSQL DDL text — the inverse of what PostgreSqlSchemaReader read in.
// DataTypeSpec.ToString() is engine-neutral and renders "timestamp with time zone(6)", which PostgreSQL does
// not parse: the precision goes before the time-zone words. So the type text is built here, not taken from
// ToString(), and the one awkward case (temporal precision) is handled explicitly.
internal static class PgTypeText
{
    public static string Declare(DataTypeSpec type)
    {
        if (type.IsUserDefined)
        {
            var quoter = PostgreSqlQuoter.Instance;
            return type.Schema is null
                ? quoter.Quote(type.Name)
                : $"{quoter.Quote(type.Schema)}.{quoter.Quote(type.Name)}";
        }

        if (type.MaxLength is int length)
        {
            return $"{type.Name}({length})";
        }

        if (type.Precision is int precision)
        {
            if (TemporalWithZone(type.Name) is var (head, tail) && head is not null)
            {
                return $"{head}({precision}){tail}";
            }

            return type.Scale is int scale ? $"{type.Name}({precision},{scale})" : $"{type.Name}({precision})";
        }

        return type.Name;
    }

    // "timestamp with time zone" -> ("timestamp", " with time zone") so the precision can be slotted between.
    // Anything without a time-zone suffix returns (null, _) and is rendered the ordinary way.
    private static (string? Head, string Tail) TemporalWithZone(string name)
    {
        foreach (var suffix in (ReadOnlySpan<string>)[" without time zone", " with time zone"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (name[..^suffix.Length], suffix);
            }
        }

        return (null, string.Empty);
    }

    public static string? ReferentialAction(ReferentialAction action) => action switch
    {
        Core.Model.ReferentialAction.Cascade => "CASCADE",
        Core.Model.ReferentialAction.SetNull => "SET NULL",
        Core.Model.ReferentialAction.SetDefault => "SET DEFAULT",
        // NO ACTION is PostgreSQL's default; emitting it is noise, so the caller omits the clause.
        _ => null,
    };

    public static string IndexMethod(IndexKind kind) => kind switch
    {
        IndexKind.Hash => "hash",
        IndexKind.Gin => "gin",
        IndexKind.Gist => "gist",
        IndexKind.Brin => "brin",
        IndexKind.Spgist => "spgist",
        _ => "btree",
    };
}
