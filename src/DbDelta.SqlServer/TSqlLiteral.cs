using System.Globalization;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

internal static class TSqlLiteral
{
    private static readonly HashSet<string> Numeric =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bigint", "int", "smallint", "tinyint", "decimal", "numeric", "float", "real",
            "money", "smallmoney"
        };

    private static readonly HashSet<string> Binary =
        new(StringComparer.OrdinalIgnoreCase) { "binary", "varbinary", "image", "timestamp", "rowversion" };

    private static readonly HashSet<string> Quoted =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "char", "varchar", "nchar", "nvarchar", "text", "ntext", "xml", "json",
            "uniqueidentifier", "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "time"
        };

    // Values arrive as strings from the row reader, so the column type decides how to write them back.
    // Getting this wrong does not fail loudly — it writes the wrong data — so unknown types stay quoted
    // rather than being guessed as numeric.
    public static string For(TableDefinition table, string column, string? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        var type = table.Columns
            .FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
            ?.DataType.Name;

        if (type is null)
        {
            return Quote(value);
        }

        if (string.Equals(type, "BIT", StringComparison.OrdinalIgnoreCase))
        {
            return bool.TryParse(value, out var flag)
                ? flag ? "1" : "0"
                : value.Trim() is "1" ? "1" : "0";
        }

        if (Numeric.Contains(type))
        {
            return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? value
                : Quote(value);
        }

        if (Binary.Contains(type))
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : $"0x{value}";
        }

        return Quoted.Contains(type) ? Quote(value) : Quote(value);
    }

    private static string Quote(string value) =>
        $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
