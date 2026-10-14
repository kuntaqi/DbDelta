using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

// Pure dialect mapping, kept apart from the reader so the conventions that feed the comparer can be tested
// without a database. Two of them are load-bearing: type names are rendered to a canonical lowercase string
// (the comparer reduces a column type to DataTypeSpec.ToString() and compares it ordinally, so casing drift
// becomes a phantom diff), and a temporal type's fractional-seconds digits go in Precision, which is the
// convention NarrowingAnalyzer reads. Names arrive already lowercase from format_type(atttypid, null).
internal static class PgTypeMapper
{
    public static DataTypeSpec Map(
        string typeName,
        int? charMaxLength,
        int? numericPrecision,
        int? numericScale,
        int? datetimePrecision,
        bool isUserDefined = false,
        string? schema = null)
    {
        if (isUserDefined)
        {
            // A domain/enum/composite carries its own size in its own definition, so the reference stores no
            // length — the same stance SqlTypeMapper takes for an alias type.
            return new DataTypeSpec(typeName, IsUserDefined: true, Schema: schema);
        }

        if (charMaxLength is int length)
        {
            return new DataTypeSpec(typeName, MaxLength: length);
        }

        // numeric_precision is also reported for integer and float types, where it is inherent and not part
        // of the written type — so precision is taken only for the arbitrary-precision numeric family.
        if (numericPrecision is int precision && (typeName == "numeric" || typeName == "decimal"))
        {
            return new DataTypeSpec(typeName, Precision: precision, Scale: numericScale);
        }

        if (datetimePrecision is int fractional && IsTemporal(typeName))
        {
            return new DataTypeSpec(typeName, Precision: fractional);
        }

        return new DataTypeSpec(typeName);
    }

    // "time", "time without time zone", "timestamp", "timestamp with time zone" all begin "time"; interval is
    // the other type that carries a fractional-seconds precision.
    private static bool IsTemporal(string typeName) =>
        typeName.StartsWith("time", StringComparison.Ordinal)
        || typeName.StartsWith("interval", StringComparison.Ordinal);

    // pg_constraint.confupdtype / confdeltype. 'r' (restrict) has no model counterpart; it collapses to
    // NoAction, which is its nearest neighbour (both forbid the change) — a documented fidelity loss.
    public static ReferentialAction MapReferentialAction(string code) => code switch
    {
        "c" => ReferentialAction.Cascade,
        "n" => ReferentialAction.SetNull,
        "d" => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };

    // IndexKind is SQL-Server-derived and has no PG access methods. 'hash' maps cleanly; btree/gin/gist/brin/
    // spgist all collapse to Rowstore, losing method fidelity until the enum grows PG values.
    public static IndexKind MapIndexKind(string accessMethod) =>
        string.Equals(accessMethod, "hash", StringComparison.Ordinal) ? IndexKind.Hash : IndexKind.Rowstore;
}
