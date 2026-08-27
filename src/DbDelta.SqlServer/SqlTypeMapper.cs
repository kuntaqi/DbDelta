using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

public static class SqlTypeMapper
{
    private static readonly HashSet<string> DoubleByteTypes =
        new(StringComparer.OrdinalIgnoreCase) { "nchar", "nvarchar" };

    private static readonly HashSet<string> LengthTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "char", "varchar", "nchar", "nvarchar", "binary", "varbinary"
        };

    private static readonly HashSet<string> PrecisionScaleTypes =
        new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };

    private static readonly HashSet<string> ScaleOnlyTypes =
        new(StringComparer.OrdinalIgnoreCase) { "datetime2", "datetimeoffset", "time" };

    // sys.types stores built-in names lowercase; DDL and the UI both read better uppercase. User
    // defined types keep their original casing, which is the name the emitter has to write back.
    private static readonly HashSet<string> BuiltInTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bigint", "binary", "bit", "char", "date", "datetime", "datetime2", "datetimeoffset",
            "decimal", "float", "geography", "geometry", "hierarchyid", "image", "int", "json",
            "money", "nchar", "ntext", "numeric", "nvarchar", "real", "smalldatetime", "smallint",
            "smallmoney", "sql_variant", "sysname", "text", "time", "timestamp", "tinyint",
            "uniqueidentifier", "varbinary", "varchar", "xml"
        };

    private static string Canonical(string typeName) =>
        BuiltInTypes.Contains(typeName) ? typeName.ToUpperInvariant() : typeName;

    // sys.columns.max_length is a byte count, and -1 means MAX. Reporting the raw value would make
    // NVARCHAR(20) look like NVARCHAR(40) and produce a phantom diff against a correct target.
    public static DataTypeSpec Map(
        string typeName,
        short maxLength,
        byte precision,
        byte scale,
        bool isUserDefined = false,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        // A reference to an alias type carries no size: the size is part of the type's own definition.
        // Reading sys.columns.max_length for one and writing it back would produce dbo.PhoneNumber(20),
        // which is not valid T-SQL — the column simply says what type it is.
        if (isUserDefined)
        {
            return new DataTypeSpec(typeName, IsUserDefined: true, Schema: schema);
        }

        var canonical = Canonical(typeName);

        if (LengthTypes.Contains(typeName))
        {
            if (maxLength == -1)
            {
                return new DataTypeSpec(canonical, IsMax: true);
            }

            var length = DoubleByteTypes.Contains(typeName) ? maxLength / 2 : maxLength;
            return new DataTypeSpec(canonical, length);
        }

        if (PrecisionScaleTypes.Contains(typeName))
        {
            return new DataTypeSpec(canonical, Precision: precision, Scale: scale);
        }

        if (ScaleOnlyTypes.Contains(typeName))
        {
            return new DataTypeSpec(canonical, Precision: scale);
        }

        return new DataTypeSpec(canonical);
    }

    public static ReferentialAction MapAction(byte action) => action switch
    {
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction
    };

    public static RoutineKind MapRoutineKind(string type) => type.Trim() switch
    {
        "P" => RoutineKind.Procedure,
        "FN" => RoutineKind.ScalarFunction,
        _ => RoutineKind.TableValuedFunction
    };
}
