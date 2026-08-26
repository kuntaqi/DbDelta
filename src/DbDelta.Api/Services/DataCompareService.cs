using System.Diagnostics;
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

    // Only the key+hash pass runs here, for every table at once. That is the cheap half of the design —
    // no row data moves — but it is still one pass per table, so it is a deliberate action rather than
    // something that happens whenever the screen opens.
    // Measured against a real 263-table database: sequentially this took over nine minutes, which is
    // not a request anyone will wait out. Each table is an independent pair of streams, so they run
    // concurrently, and tables past a size limit are reported as skipped rather than quietly scanned
    // for minutes each.
    private const int ScanConcurrency = 8;
    private const int ScanBatchSize = 12;

    public async Task<TableScanResponse> ScanAsync(
        CompareSession session,
        long maxTableBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var stopwatch = Stopwatch.StartNew();
        var targets = session.Target.Tables.ToDictionary(t => t.Identity);

        var volume = await _provider.CreateVolumeReader(session.SourceConnectionString)
            .ReadAsync(cancellationToken).ConfigureAwait(false);
        var sizes = volume.Tables.ToDictionary(t => t.Table, t => t.TotalBytes);

        var candidates = session.Source.Tables
            .OrderBy(t => t.Identity.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<TableScanRow>();
        var comparable = new List<FingerprintRequest>();

        foreach (var source in candidates)
        {
            var name = source.Identity.QualifiedName;
            var bytes = sizes.GetValueOrDefault(source.Identity);

            if (!targets.TryGetValue(source.Identity, out var target))
            {
                rows.Add(new TableScanRow(name, false, "only on source", false, 0, 0));
                continue;
            }

            if (maxTableBytes > 0 && bytes > maxTableBytes)
            {
                rows.Add(new TableScanRow(
                    name, false, $"skipped, {bytes / 1024 / 1024} MB is over the scan limit", false, 0, 0));
                continue;
            }

            var key = session.KeyFor(source);
            if (key.Count == 0)
            {
                rows.Add(new TableScanRow(name, false, "no primary key", false, 0, 0));
                continue;
            }

            var columns = ColumnSetResolver.Resolve(
                source, target, new DataCompareRequest { Table = source.Identity, KeyColumns = key });

            if (!columns.CanCompare)
            {
                rows.Add(new TableScanRow(name, false, "no comparable columns", false, 0, 0));
                continue;
            }

            comparable.Add(new FingerprintRequest(source, key, columns.ComparedColumns));
        }

        // Both sides in parallel, and inside each side the reader batches tables per round trip. The
        // server still scans, but nothing crosses the wire except three numbers per table.
        var sourceTask = Fingerprints(session.SourceConnectionString, comparable, cancellationToken);
        var targetTask = Fingerprints(session.TargetConnectionString, TargetSide(comparable, targets), cancellationToken);
        await Task.WhenAll(sourceTask, targetTask).ConfigureAwait(false);

        var sourcePrints = await sourceTask.ConfigureAwait(false);
        var targetPrints = await targetTask.ConfigureAwait(false);

        foreach (var request in comparable)
        {
            var name = request.Table.Identity.QualifiedName;
            var left = sourcePrints.GetValueOrDefault(request.Table.Identity);
            var right = targetPrints.GetValueOrDefault(request.Table.Identity);

            if (left is null || right is null)
            {
                rows.Add(new TableScanRow(name, false, "could not be read", false, 0, 0));
                continue;
            }

            rows.Add(new TableScanRow(name, true, null, !left.Matches(right), left.RowCount, right.RowCount));
        }

        rows = rows.OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase).ToList();
        stopwatch.Stop();

        session.Scan.Clear();
        foreach (var row in rows)
        {
            session.Scan[row.Table] = row;
        }

        return new TableScanResponse(
            stopwatch.ElapsedMilliseconds,
            rows.Count(r => r.Comparable),
            rows.Count(r => r.Differs),
            rows.Count(r => !r.Comparable),
            rows.Count(r => r.Reason?.StartsWith("skipped", StringComparison.Ordinal) == true),
            maxTableBytes,
            rows);
    }

    public static TableScanResponse? CachedScan(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Scan.Count == 0)
        {
            return null;
        }

        var rows = session.Scan.Values
            .OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TableScanResponse(
            0,
            rows.Count(r => r.Comparable),
            rows.Count(r => r.Differs),
            rows.Count(r => !r.Comparable),
            rows.Count(r => r.Reason?.StartsWith("skipped", StringComparison.Ordinal) == true),
            0,
            rows);
    }

    // The same tables, but paired with the target's own definitions: column types differ between sides
    // after drift, and the digest expression is built from whichever side it will run on.
    private static IReadOnlyList<FingerprintRequest> TargetSide(
        IReadOnlyList<FingerprintRequest> requests,
        Dictionary<ObjectIdentity, TableDefinition> targets) =>
        requests
            .Where(r => targets.ContainsKey(r.Table.Identity))
            .Select(r => new FingerprintRequest(targets[r.Table.Identity], r.KeyColumns, r.Columns))
            .ToList();

    private async Task<Dictionary<ObjectIdentity, TableFingerprint>> Fingerprints(
        string connectionString,
        IReadOnlyList<FingerprintRequest> requests,
        CancellationToken cancellationToken)
    {
        var reader = _provider.CreateFingerprintReader(connectionString);
        var prints = new Dictionary<ObjectIdentity, TableFingerprint>();

        // Batches run concurrently so one slow table does not hold the rest up. A table that cannot be
        // read loses only its own batch entry; the scan still reports on everything else.
        var batches = requests.Chunk(ScanBatchSize).ToList();
        var results = new IReadOnlyList<TableFingerprint>[batches.Count];

        await Parallel.ForAsync(0, batches.Count,
            new ParallelOptions { MaxDegreeOfParallelism = ScanConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                try
                {
                    results[index] = await reader.ReadAsync(batches[index], token).ConfigureAwait(false);
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    results[index] = [];
                }
            }).ConfigureAwait(false);

        foreach (var print in results.Where(r => r is not null).SelectMany(r => r))
        {
            prints[print.Table] = print;
        }

        return prints;
    }

    // What a table's key currently is, and what it could be. A table without a primary key is not
    // guessed at: it waits here until columns are picked and verified.
    public KeyChoiceResponse KeyOptions(CompareSession session, string table)
    {
        ArgumentNullException.ThrowIfNull(session);

        var source = Find(session.Source, table)
            ?? throw new InvalidOperationException($"{table} is not a table on the source.");

        var target = Find(session.Target, table);
        var targetColumns = target?.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = source.Columns
            .Where(c => c.ComputedExpression is null)
            .Where(c => target is null || targetColumns.Contains(c.Name))
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => new KeyCandidate(c.Name, c.DataType.ToString(), c.IsNullable))
            .ToList();

        var chosen = session.KeyFor(source);

        return new KeyChoiceResponse(
            source.Identity.QualifiedName,
            chosen,
            source.PrimaryKey is not null,
            candidates,
            chosen.Count == 0
                ? "This table has no primary key. Pick the columns that identify a row, and they will be "
                    + "checked for uniqueness on both sides before the data can be compared."
                : null);
    }

    public async Task<KeyChoiceResponse> ChooseKeyAsync(
        CompareSession session,
        KeyChoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var source = Find(session.Source, request.Table)
            ?? throw new InvalidOperationException($"{request.Table} is not a table on the source.");

        if (request.Columns.Count == 0)
        {
            session.KeyOverrides.Remove(source.Identity);
            return KeyOptions(session, request.Table);
        }

        var unknown = request.Columns
            .Where(c => !source.Columns.Any(x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"Not a column of {request.Table}: {string.Join(", ", unknown)}.");
        }

        var target = Find(session.Target, request.Table)
            ?? throw new InvalidOperationException($"{request.Table} is not on the target, so there is nothing to compare against.");

        // Both sides, because a key that is unique on the source and repeated on the target still breaks
        // the merge join — and it is the target that gets written to.
        var sourceCheck = await _provider.CreateKeyUniquenessChecker(session.SourceConnectionString)
            .CheckAsync(source, request.Columns, cancellationToken).ConfigureAwait(false);

        if (!sourceCheck.IsUnique)
        {
            return Rejected(session, request, sourceCheck.Explain("Source"));
        }

        var targetCheck = await _provider.CreateKeyUniquenessChecker(session.TargetConnectionString)
            .CheckAsync(target, request.Columns, cancellationToken).ConfigureAwait(false);

        if (!targetCheck.IsUnique)
        {
            return Rejected(session, request, targetCheck.Explain("Target"));
        }

        session.KeyOverrides[source.Identity] = request.Columns;
        return KeyOptions(session, request.Table);
    }

    private KeyChoiceResponse Rejected(CompareSession session, KeyChoiceRequest request, string problem)
    {
        var options = KeyOptions(session, request.Table);
        return options with { Problem = problem };
    }

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

        if (session.KeyFor(table).Count == 0)
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
            KeyColumns = session.KeyFor(source),
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
                var key = session.KeyFor(table);

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
        var key = session.KeyFor(source);

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
