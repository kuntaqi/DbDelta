using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

public static class ColumnSetResolver
{
    private static readonly HashSet<string> NotComparableTypes =
        new(StringComparer.OrdinalIgnoreCase) { "timestamp", "rowversion" };

    // Comparing over the intersection is the only workable choice when the schemas have drifted, but
    // dropping the rest silently is how a tool quietly under-reports. Every omission comes back with
    // a reason so the UI can show it rather than the user discovering it later.
    public static ColumnSetResolution Resolve(
        TableDefinition source,
        TableDefinition target,
        DataCompareRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(request.KeyColumns, StringComparer.OrdinalIgnoreCase);

        var compared = new List<string>();
        var excluded = new List<ColumnExclusion>();

        foreach (var column in source.Columns.OrderBy(c => c.OrdinalPosition))
        {
            if (keys.Contains(column.Name))
            {
                continue;
            }

            if (!targetColumns.ContainsKey(column.Name))
            {
                excluded.Add(new ColumnExclusion(column.Name, "only on source"));
                continue;
            }

            if (NotComparableTypes.Contains(column.DataType.Name))
            {
                excluded.Add(new ColumnExclusion(column.Name, "not comparable"));
                continue;
            }

            if (column.ComputedExpression is not null)
            {
                excluded.Add(new ColumnExclusion(column.Name, "computed"));
                continue;
            }

            if (request.IgnoredColumns.Contains(column.Name))
            {
                excluded.Add(new ColumnExclusion(column.Name, "excluded by rule"));
                continue;
            }

            compared.Add(column.Name);
        }

        var sourceColumns = source.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in target.Columns.OrderBy(c => c.OrdinalPosition))
        {
            if (!sourceColumns.Contains(column.Name) && !keys.Contains(column.Name))
            {
                excluded.Add(new ColumnExclusion(column.Name, "only on target"));
            }
        }

        return new ColumnSetResolution
        {
            ComparedColumns = compared,
            ExcludedColumns = excluded,
            TotalSourceColumns = source.Columns.Count
        };
    }

    // A table with no primary key is not compared on a guess. Treating every column as the key looks
    // like it works until duplicate rows make it silently wrong.
    public static IReadOnlyList<string> DefaultKeyFor(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.PrimaryKey?.Columns.Select(c => c.Name).ToList() ?? [];
    }
}
