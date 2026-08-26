using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

internal static class RowDigestBuilder
{
    private static readonly SqlServerQuoter Q = SqlServerQuoter.Instance;

    private static readonly HashSet<string> DateTimeTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "time"
        };

    private static readonly HashSet<string> FloatTypes =
        new(StringComparer.OrdinalIgnoreCase) { "float", "real" };

    private static readonly HashSet<string> BinaryTypes =
        new(StringComparer.OrdinalIgnoreCase) { "binary", "varbinary", "image", "timestamp", "rowversion" };

    // Each column contributes "|<bytes>:<text>". CONCAT_WS alone is unsafe here because it drops NULLs
    // outright, so ('a', NULL, 'b') and ('a', 'b', NULL) would hash the same. The length prefix also
    // separates a NULL (written as "-") from an empty string (written as "0:").
    // CONCAT takes between 2 and 254 arguments: a single-column key would be a one-argument call and
    // a wide table would exceed the ceiling, so both ends are handled rather than left to fail at runtime.
    private const int MaxConcatArguments = 250;

    public static string Concatenation(TableDefinition table, IEnumerable<string> columns)
    {
        var parts = columns.Select(name =>
        {
            var column = table.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            var quoted = Q.Quote(name);
            var text = TextExpression(quoted, column?.DataType.Name);

            return $"CONCAT(N'|', COALESCE(CONVERT(nvarchar(20), DATALENGTH({quoted})), N'-'), N':', COALESCE({text}, N''))";
        }).ToList();

        return Combine(parts);
    }

    private static string Combine(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
        {
            return "N''";
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

        if (parts.Count <= MaxConcatArguments)
        {
            return $"CONCAT({string.Join(", ", parts)})";
        }

        var chunks = parts
            .Chunk(MaxConcatArguments)
            .Select(chunk => Combine(chunk))
            .ToList();

        return Combine(chunks);
    }

    public static string HashExpression(TableDefinition table, IEnumerable<string> columns) =>
        $"CONVERT(varchar(64), HASHBYTES('SHA2_256', {Concatenation(table, columns)}), 2)";

    // Styles matter: the default conversion of a float loses digits and of a datetime is culture
    // shaped, either of which would report equal rows as different across two servers.
    private static string TextExpression(string quoted, string? typeName)
    {
        if (typeName is null)
        {
            return $"CONVERT(nvarchar(max), {quoted})";
        }

        if (DateTimeTypes.Contains(typeName))
        {
            return $"CONVERT(nvarchar(max), {quoted}, 126)";
        }

        if (FloatTypes.Contains(typeName))
        {
            return $"CONVERT(nvarchar(max), {quoted}, 3)";
        }

        if (BinaryTypes.Contains(typeName))
        {
            return $"CONVERT(nvarchar(max), CONVERT(varbinary(max), {quoted}), 2)";
        }

        return $"CONVERT(nvarchar(max), {quoted})";
    }
}
