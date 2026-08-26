using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

[Collection(nameof(LocalDbCollection))]
public sealed class DataCompareTests
{
    private readonly LocalDbFixture _fixture;

    public DataCompareTests(LocalDbFixture fixture) => _fixture = fixture;

    private static SqlServerRowHashReader ReaderFor(string database) =>
        new(LocalDbFixture.ConnectionStringFor(database));

    private async Task<(TableDefinition Source, TableDefinition Target)> TablesAsync(string name)
    {
        var source = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
        var target = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase)).ReadAsync();

        return (source.Tables.Single(t => t.Identity.Name == name),
                target.Tables.Single(t => t.Identity.Name == name));
    }

    private async Task<DataCompareResult> CompareAsync(string table, TableDataMode mode, int top = 100)
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target) = await TablesAsync(table);

        var request = new DataCompareRequest
        {
            Table = source.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(source),
            Mode = mode,
            TopCount = top
        };

        var columns = ColumnSetResolver.Resolve(source, target, request);

        return await DataComparer.CompareAsync(
            ReaderFor(LocalDbFixture.SourceDatabase)
                .StreamAsync(source, request, columns.ComparedColumns, RowSetSide.Source),
            ReaderFor(LocalDbFixture.TargetDatabase)
                .StreamAsync(target, request, columns.ComparedColumns, RowSetSide.Target),
            new DataCompareSettings
            {
                Mode = mode,
                ComparedColumns = columns.ComparedColumns,
                ExcludedColumns = columns.ExcludedColumns
            });
    }

    // Source Category holds 1,2,3; target holds 1, a changed 2, and a 4 that is not in the source.
    [SkippableFact]
    public async Task All_rows_classifies_every_row_against_real_data()
    {
        var result = await CompareAsync("Category", TableDataMode.AllRows);

        Assert.Equal(1, result.InsertCount);
        Assert.Equal(1, result.UpdateCount);
        Assert.Equal(1, result.DeleteCount);
        Assert.Equal(1, result.SameCount);
    }

    [SkippableFact]
    public async Task Top_n_limits_the_source_window_and_drops_no_target_rows()
    {
        // Top 2 by key takes rows 1 and 2 only, so the insert of 3 falls outside the window and the
        // target-only 4 must not turn into a delete.
        var result = await CompareAsync("Category", TableDataMode.TopN, top: 2);

        Assert.True(result.DeletesSuppressed);
        Assert.Equal(0, result.InsertCount);
        Assert.Equal(1, result.UpdateCount);
        Assert.Equal(0, result.DeleteCount);
    }

    [SkippableFact]
    public async Task Top_n_is_reproducible()
    {
        var first = await CompareAsync("Category", TableDataMode.TopN, top: 2);
        var second = await CompareAsync("Category", TableDataMode.TopN, top: 2);

        Assert.Equal(
            first.Differences.Select(d => (d.Key, d.Classification)),
            second.Differences.Select(d => (d.Key, d.Classification)));
    }

    [SkippableFact]
    public async Task Comparing_a_database_against_itself_finds_nothing()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, _) = await TablesAsync("Category");
        var request = new DataCompareRequest
        {
            Table = source.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(source)
        };
        var columns = ColumnSetResolver.Resolve(source, source, request);

        var result = await DataComparer.CompareAsync(
            ReaderFor(LocalDbFixture.SourceDatabase)
                .StreamAsync(source, request, columns.ComparedColumns, RowSetSide.Source),
            ReaderFor(LocalDbFixture.SourceDatabase)
                .StreamAsync(source, request, columns.ComparedColumns, RowSetSide.Target),
            new DataCompareSettings { Mode = TableDataMode.AllRows, ComparedColumns = columns.ComparedColumns });

        Assert.False(result.HasChanges);
        Assert.Equal(3, result.SameCount);
    }

    [SkippableFact]
    public async Task A_column_missing_on_the_target_is_excluded_and_explained()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target) = await TablesAsync("Company");
        var request = new DataCompareRequest
        {
            Table = source.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(source)
        };

        var columns = ColumnSetResolver.Resolve(source, target, request);

        Assert.DoesNotContain("RatingBand", columns.ComparedColumns);
        Assert.Contains(columns.ExcludedColumns, e => e.Column == "RatingBand" && e.Reason == "only on source");
        Assert.Contains("Segment", columns.ComparedColumns);
    }

    // The reason the digest is length-prefixed rather than a plain CONCAT_WS: that function drops
    // NULLs, so (NULL,'x') and ('x',NULL) would produce the same string and hash identically.
    [SkippableFact]
    public async Task Null_and_empty_and_position_all_change_the_hash()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (digest, _) = await TablesAsync("Digest");
        var request = new DataCompareRequest
        {
            Table = digest.Identity,
            KeyColumns = ["DigestId"]
        };

        var rows = new List<KeyHashRow>();
        await foreach (var row in ReaderFor(LocalDbFixture.SourceDatabase)
            .StreamAsync(digest, request, ["A", "B"], RowSetSide.Source))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.Hash).Distinct().Count());
    }

    // CONVERT(nvarchar, float, 3) keeps the 17 digits a double needs, but throws "arithmetic overflow"
    // on any negative value — even into nvarchar(max). Found on a real database where it silently took
    // ten tables out of the scan, so the sign is handled separately now.
    [SkippableFact]
    public async Task A_negative_float_does_not_break_the_digest()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (digest, _) = await TablesAsync("Digest");
        var request = new DataCompareRequest { Table = digest.Identity, KeyColumns = ["DigestId"] };

        var rows = new List<KeyHashRow>();
        await foreach (var row in ReaderFor(LocalDbFixture.SourceDatabase)
            .StreamAsync(digest, request, ["A", "B", "Amount"], RowSetSide.Source))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.Hash).Distinct().Count());
    }

    [SkippableFact]
    public async Task Keys_arrive_in_the_order_the_merge_join_requires()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, _) = await TablesAsync("Category");
        var request = new DataCompareRequest
        {
            Table = source.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(source)
        };

        var keys = new List<string>();
        await foreach (var row in ReaderFor(LocalDbFixture.SourceDatabase)
            .StreamAsync(source, request, ["Name"], RowSetSide.Source))
        {
            keys.Add(row.Key);
        }

        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [SkippableFact]
    public async Task A_table_without_a_key_is_refused_rather_than_guessed()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, _) = await TablesAsync("Category");
        var request = new DataCompareRequest { Table = source.Identity, KeyColumns = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in ReaderFor(LocalDbFixture.SourceDatabase)
                .StreamAsync(source, request, ["Name"], RowSetSide.Source))
            {
                break;
            }
        });
    }
}
