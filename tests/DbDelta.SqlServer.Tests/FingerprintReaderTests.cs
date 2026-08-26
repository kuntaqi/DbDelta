using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

// The screening pass. It has to agree with the exact merge join about *whether* a table differs; it
// deliberately says nothing about which rows.
[Collection(nameof(LocalDbCollection))]
public sealed class FingerprintReaderTests
{
    private readonly LocalDbFixture _fixture;

    public FingerprintReaderTests(LocalDbFixture fixture) => _fixture = fixture;

    private static async Task<IReadOnlyList<FingerprintRequest>> RequestsAsync(string connectionString)
    {
        var schema = await new SqlServerSchemaReader(connectionString).ReadAsync();

        return schema.Tables
            .Select(table => new
            {
                Table = table,
                Key = ColumnSetResolver.DefaultKeyFor(table)
            })
            .Where(x => x.Key.Count > 0)
            .Select(x => new FingerprintRequest(
                x.Table,
                x.Key,
                x.Table.Columns.Select(c => c.Name).Except(x.Key, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    [SkippableFact]
    public async Task A_database_fingerprinted_against_itself_matches_everywhere()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var connectionString = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var requests = await RequestsAsync(connectionString);
        var reader = new SqlServerFingerprintReader(connectionString);

        var first = await reader.ReadAsync(requests);
        var second = await reader.ReadAsync(requests);

        Assert.NotEmpty(first);
        Assert.Equal(requests.Count, first.Count);

        foreach (var print in first)
        {
            var other = second.Single(p => p.Table == print.Table);
            Assert.True(print.Matches(other), $"{print.Table.QualifiedName} should match itself");
        }
    }

    // The point of the screen: it must reach the same verdict as the exact compare about which tables
    // differ, or hiding "matching" tables would hide real work.
    [SkippableFact]
    public async Task The_screen_agrees_with_the_exact_compare_about_which_tables_differ()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var sourceConnection = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var targetConnection = LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase);

        var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
        var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();
        var targetsByIdentity = target.Tables.ToDictionary(t => t.Identity);

        foreach (var sourceTable in source.Tables)
        {
            var key = ColumnSetResolver.DefaultKeyFor(sourceTable);
            if (key.Count == 0 || !targetsByIdentity.TryGetValue(sourceTable.Identity, out var targetTable))
            {
                continue;
            }

            var request = new DataCompareRequest { Table = sourceTable.Identity, KeyColumns = key };
            var columns = ColumnSetResolver.Resolve(sourceTable, targetTable, request);
            if (!columns.CanCompare)
            {
                continue;
            }

            var exact = await DataComparer.CompareAsync(
                new SqlServerRowHashReader(sourceConnection)
                    .StreamAsync(sourceTable, request, columns.ComparedColumns, RowSetSide.Source),
                new SqlServerRowHashReader(targetConnection)
                    .StreamAsync(targetTable, request, columns.ComparedColumns, RowSetSide.Target),
                new DataCompareSettings { Mode = TableDataMode.AllRows, ComparedColumns = columns.ComparedColumns });

            var left = (await new SqlServerFingerprintReader(sourceConnection)
                .ReadAsync([new FingerprintRequest(sourceTable, key, columns.ComparedColumns)])).Single();
            var right = (await new SqlServerFingerprintReader(targetConnection)
                .ReadAsync([new FingerprintRequest(targetTable, key, columns.ComparedColumns)])).Single();

            Assert.Equal(exact.HasChanges, !left.Matches(right));
        }
    }

    [SkippableFact]
    public async Task An_empty_request_list_makes_no_round_trip()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var reader = new SqlServerFingerprintReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase));

        Assert.Empty(await reader.ReadAsync([]));
    }

    [SkippableFact]
    public async Task Row_counts_come_back_exactly()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var connectionString = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var requests = await RequestsAsync(connectionString);

        var prints = await new SqlServerFingerprintReader(connectionString).ReadAsync(requests);
        var category = prints.Single(p => p.Table.Name == "Category");

        Assert.Equal(3, category.RowCount);
    }
}
