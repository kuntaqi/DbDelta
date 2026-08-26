using DbDelta.Api.Contracts;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

public sealed class DataCompareService
{
    private const int MaxRowsShown = 200;

    private readonly IDatabaseProvider _provider;

    public DataCompareService(IDatabaseProvider provider) => _provider = provider;

    public DataSelectionResponse Select(CompareSession session, DataSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var table = Find(session.Source, request.Table)
            ?? throw new InvalidOperationException($"{request.Table} is not a table on the source.");

        if (!request.Selected)
        {
            session.DataSelections.Remove(table.Identity);
            return Selection(session);
        }

        if (ColumnSetResolver.DefaultKeyFor(table).Count == 0)
        {
            throw new InvalidOperationException(
                $"{request.Table} has no primary key, so its rows cannot be addressed. Pick key columns first.");
        }

        var mode = Enum.Parse<TableDataMode>(request.Mode, true);

        if (mode == TableDataMode.SchemaOnly)
        {
            session.DataSelections.Remove(table.Identity);
            return Selection(session);
        }

        session.DataSelections[table.Identity] = new DataSelection(mode, request.TopCount, request.Filter);
        return Selection(session);
    }

    public static DataSelectionResponse Selection(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new DataSelectionResponse(session.DataSelections
            .Select(pair => new SelectedTable(pair.Key.QualifiedName, pair.Value.Mode.ToString(), pair.Value.TopCount))
            .OrderBy(s => s.Table, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    // Turns a selected table into the rows the emitter needs. Row values are fetched here and only
    // here: the counts came from hashes, so nothing is moved until something is actually being written.
    public async Task<TableDataChanges?> ChangesAsync(
        CompareSession session,
        ObjectIdentity table,
        DataSelection selection,
        CancellationToken cancellationToken)
    {
        var source = session.Source.Tables.FirstOrDefault(t => t.Identity == table);
        var target = session.Target.Tables.FirstOrDefault(t => t.Identity == table);

        if (source is null || target is null)
        {
            return null;
        }

        var request = new DataCompareRequest
        {
            Table = table,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(source),
            Mode = selection.Mode,
            TopCount = selection.TopCount,
            FilterPredicate = selection.Filter
        };

        var columns = ColumnSetResolver.Resolve(source, target, request);
        if (!columns.CanCompare)
        {
            return null;
        }

        var result = await DataComparer.CompareAsync(
            _provider.CreateRowHashReader(session.SourceConnectionString)
                .StreamAsync(source, request, columns.ComparedColumns, RowSetSide.Source, cancellationToken),
            _provider.CreateRowHashReader(session.TargetConnectionString)
                .StreamAsync(target, request, columns.ComparedColumns, RowSetSide.Target, cancellationToken),
            new DataCompareSettings { Mode = selection.Mode, ComparedColumns = columns.ComparedColumns },
            cancellationToken).ConfigureAwait(false);

        if (!result.HasChanges)
        {
            return null;
        }

        var fetched = columns.ComparedColumns.Concat(request.KeyColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sourceRows = (await _provider.CreateRowDetailReader(session.SourceConnectionString)
            .FetchAsync(source, request, fetched,
                result.Differences.Where(d => d.Classification != RowClassification.Delete).Select(d => d.Key).ToList(),
                cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Key, StringComparer.Ordinal);

        var targetRows = (await _provider.CreateRowDetailReader(session.TargetConnectionString)
            .FetchAsync(target, request, fetched,
                result.Differences.Where(d => d.Classification == RowClassification.Delete).Select(d => d.Key).ToList(),
                cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Key, StringComparer.Ordinal);

        var changes = result.Differences
            .Select(difference =>
            {
                var row = difference.Classification == RowClassification.Delete
                    ? targetRows.GetValueOrDefault(difference.Key)
                    : sourceRows.GetValueOrDefault(difference.Key);

                if (row is null)
                {
                    return null;
                }

                var keys = request.KeyColumns.ToDictionary(
                    c => c, c => row.Values.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase);

                return new DataChange(
                    difference.Key,
                    string.Join(", ", keys.Values.Select(v => v ?? "NULL")),
                    difference.Classification,
                    row.Values,
                    keys);
            })
            .OfType<DataChange>()
            .ToList();

        var volume = await _provider.CreateVolumeReader(session.TargetConnectionString)
            .ReadAsync(cancellationToken).ConfigureAwait(false);

        return new TableDataChanges
        {
            Table = source,
            KeyColumns = request.KeyColumns,
            Columns = columns.ComparedColumns,
            Changes = changes,
            TargetRowCount = volume.Tables.FirstOrDefault(v => v.Table == table)?.RowCount ?? 0
        };
    }

    public async Task<VolumeSummary> VolumeAsync(CompareSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var source = await _provider.CreateVolumeReader(session.SourceConnectionString)
            .ReadAsync(cancellationToken).ConfigureAwait(false);
        var target = await _provider.CreateVolumeReader(session.TargetConnectionString)
            .ReadAsync(cancellationToken).ConfigureAwait(false);

        var targetByTable = target.Tables.ToDictionary(t => t.Table);
        var targetTables = session.Target.Tables.Select(t => t.Identity).ToHashSet();

        var rows = session.Source.Tables
            .Select(table =>
            {
                var sourceVolume = source.Tables.FirstOrDefault(v => v.Table == table.Identity);
                var targetVolume = targetByTable.GetValueOrDefault(table.Identity);
                var key = ColumnSetResolver.DefaultKeyFor(table);

                return new TableRow(
                    table.Identity.QualifiedName,
                    sourceVolume?.RowCount ?? 0,
                    sourceVolume?.TotalBytes ?? 0,
                    targetVolume?.RowCount ?? 0,
                    targetVolume?.TotalBytes ?? 0,
                    key,
                    key.Count > 0,
                    targetTables.Contains(table.Identity));
            })
            .OrderByDescending(r => r.SourceBytes)
            .ToList();

        return new VolumeSummary(
            source.DataBytes, source.LogBytes, source.TotalRows,
            target.DataBytes, target.LogBytes, target.TotalRows,
            rows);
    }

    public async Task<DataCompareResponse> CompareAsync(
        CompareSession session,
        DataCompareRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var source = Find(session.Source, request.Table);
        var target = Find(session.Target, request.Table);

        if (source is null || target is null)
        {
            throw new InvalidOperationException($"{request.Table} is not on both sides, so its data cannot be compared.");
        }

        var mode = Enum.Parse<TableDataMode>(request.Mode, true);
        var key = ColumnSetResolver.DefaultKeyFor(source);

        if (key.Count == 0)
        {
            throw new InvalidOperationException(
                $"{request.Table} has no primary key. Choose key columns before comparing its data.");
        }

        var compareRequest = new DataCompareRequest
        {
            Table = source.Identity,
            KeyColumns = key,
            Mode = mode,
            TopCount = request.TopCount,
            FilterPredicate = request.Filter
        };

        var columns = ColumnSetResolver.Resolve(source, target, compareRequest);

        if (mode == TableDataMode.SchemaOnly || !columns.CanCompare)
        {
            return Empty(request, mode, key, columns, session, source);
        }

        var result = await DataComparer.CompareAsync(
            _provider.CreateRowHashReader(session.SourceConnectionString)
                .StreamAsync(source, compareRequest, columns.ComparedColumns, RowSetSide.Source, cancellationToken),
            _provider.CreateRowHashReader(session.TargetConnectionString)
                .StreamAsync(target, compareRequest, columns.ComparedColumns, RowSetSide.Target, cancellationToken),
            new DataCompareSettings
            {
                Mode = mode,
                ComparedColumns = columns.ComparedColumns,
                ExcludedColumns = columns.ExcludedColumns
            },
            cancellationToken).ConfigureAwait(false);

        var rows = await DetailAsync(session, source, target, compareRequest, columns, result, cancellationToken)
            .ConfigureAwait(false);

        var footprint = await FootprintAsync(session, source.Identity, cancellationToken).ConfigureAwait(false);

        return new DataCompareResponse(
            request.Table,
            mode.ToString(),
            result.DeletesSuppressed,
            key,
            columns.ComparedColumns,
            columns.ExcludedColumns.Select(e => new CellDiff(e.Column, e.Reason, null)).ToList(),
            result.InsertCount,
            result.UpdateCount,
            result.DeleteCount,
            result.SameCount,
            EstimateTransfer(result, columns.ComparedColumns.Count),
            footprint,
            rows,
            result.DeletesSuppressed
                ? "Deletes are suppressed: a limited row set cannot tell you a row was deleted, only that it fell outside the limit."
                : null);
    }

    // Only rows actually being shown get their values fetched. This is the second pass of the design:
    // the counts above came from hashes alone, without moving any row data.
    private async Task<IReadOnlyList<RowDiffDto>> DetailAsync(
        CompareSession session,
        TableDefinition source,
        TableDefinition target,
        DataCompareRequest request,
        ColumnSetResolution columns,
        DataCompareResult result,
        CancellationToken cancellationToken)
    {
        var shown = result.Differences.Take(MaxRowsShown).ToList();
        if (shown.Count == 0)
        {
            return [];
        }

        var sourceKeys = shown
            .Where(d => d.Classification is RowClassification.Insert or RowClassification.Update)
            .Select(d => d.Key)
            .ToList();

        var targetKeys = shown
            .Where(d => d.Classification is RowClassification.Delete or RowClassification.Update)
            .Select(d => d.Key)
            .ToList();

        // The canonical key is an internal artifact of the merge join, so the key columns come back
        // too and the row is labelled with values a person recognises.
        var fetched = columns.ComparedColumns.Concat(request.KeyColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sourceRows = (await _provider.CreateRowDetailReader(session.SourceConnectionString)
            .FetchAsync(source, request, fetched, sourceKeys, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(r => r.Key, StringComparer.Ordinal);

        var targetRows = (await _provider.CreateRowDetailReader(session.TargetConnectionString)
            .FetchAsync(target, request, fetched, targetKeys, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(r => r.Key, StringComparer.Ordinal);

        return shown.Select(difference =>
        {
            var left = sourceRows.GetValueOrDefault(difference.Key);
            var right = targetRows.GetValueOrDefault(difference.Key);

            var changes = new List<CellDiff>();
            var unchanged = 0;

            foreach (var column in columns.ComparedColumns)
            {
                var sourceValue = left?.Values.GetValueOrDefault(column);
                var targetValue = right?.Values.GetValueOrDefault(column);

                if (difference.Classification == RowClassification.Update
                    && string.Equals(sourceValue, targetValue, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                changes.Add(new CellDiff(column, sourceValue, targetValue));
            }

            var labelled = left ?? right;
            var display = labelled is null
                ? difference.Key
                : string.Join(", ", request.KeyColumns.Select(c => labelled.Values.GetValueOrDefault(c) ?? "NULL"));

            return new RowDiffDto(
                difference.Key,
                display,
                difference.Classification.ToString(),
                changes,
                unchanged);
        }).ToList();
    }

    private async Task<long> FootprintAsync(
        CompareSession session,
        ObjectIdentity table,
        CancellationToken cancellationToken)
    {
        var volume = await _provider.CreateVolumeReader(session.SourceConnectionString)
            .ReadAsync(cancellationToken).ConfigureAwait(false);

        return volume.Tables.FirstOrDefault(v => v.Table == table)?.TotalBytes ?? 0;
    }

    // Transfer, not footprint: what this sync would actually move. Rough by design — an estimate that
    // is honest about being one beats a precise number nobody can act on.
    private static long EstimateTransfer(DataCompareResult result, int columnCount) =>
        (result.InsertCount + result.UpdateCount) * (long)columnCount * 24;

    private static DataCompareResponse Empty(
        DataCompareRequestDto request,
        TableDataMode mode,
        IReadOnlyList<string> key,
        ColumnSetResolution columns,
        CompareSession session,
        TableDefinition source) =>
        new(request.Table,
            mode.ToString(),
            false,
            key,
            columns.ComparedColumns,
            columns.ExcludedColumns.Select(e => new CellDiff(e.Column, e.Reason, null)).ToList(),
            0, 0, 0, 0, 0,
            session.Source.Tables.FirstOrDefault(t => t.Identity == source.Identity) is null ? 0 : 0,
            [],
            mode == TableDataMode.SchemaOnly
                ? "Schema only: structure is synced, no rows move."
                : "No comparable columns once exclusions are applied.");

    private static TableDefinition? Find(Core.Model.DatabaseSchema schema, string qualifiedName) =>
        schema.Tables.FirstOrDefault(t =>
            string.Equals(t.Identity.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase));
}
