using DbDelta.Core.Data;

namespace DbDelta.Core.Tests;

public sealed class DataComparerTests
{
    private static async IAsyncEnumerable<KeyHashRow> Stream(params (string Key, string Hash)[] rows)
    {
        foreach (var (key, hash) in rows)
        {
            yield return new KeyHashRow(key, hash);
            await Task.Yield();
        }
    }

    private static DataCompareSettings Settings(TableDataMode mode = TableDataMode.AllRows) =>
        new() { Mode = mode, ComparedColumns = ["Name"] };

    [Fact]
    public async Task Identical_streams_report_no_differences()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1"), ("b", "2")),
            Stream(("a", "1"), ("b", "2")),
            Settings());

        Assert.False(result.HasChanges);
        Assert.Equal(2, result.SameCount);
    }

    [Fact]
    public async Task A_key_only_on_the_source_is_an_insert()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1"), ("b", "2")),
            Stream(("a", "1")),
            Settings());

        var difference = Assert.Single(result.Differences);
        Assert.Equal("b", difference.Key);
        Assert.Equal(RowClassification.Insert, difference.Classification);
    }

    [Fact]
    public async Task A_key_only_on_the_target_is_a_delete()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1")),
            Stream(("a", "1"), ("z", "9")),
            Settings());

        var difference = Assert.Single(result.Differences);
        Assert.Equal("z", difference.Key);
        Assert.Equal(RowClassification.Delete, difference.Classification);
    }

    [Fact]
    public async Task A_matching_key_with_a_different_hash_is_an_update()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1")),
            Stream(("a", "2")),
            Settings());

        var difference = Assert.Single(result.Differences);
        Assert.Equal(RowClassification.Update, difference.Classification);
        Assert.Equal(0, result.SameCount);
    }

    [Fact]
    public async Task Interleaved_keys_are_classified_independently()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1"), ("c", "3"), ("e", "5")),
            Stream(("b", "2"), ("c", "9"), ("d", "4")),
            Settings());

        Assert.Equal(2, result.InsertCount);
        Assert.Equal(1, result.UpdateCount);
        Assert.Equal(2, result.DeleteCount);
    }

    [Fact]
    public async Task An_empty_target_makes_everything_an_insert()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1"), ("b", "2")),
            Stream(),
            Settings());

        Assert.Equal(2, result.InsertCount);
        Assert.Equal(0, result.DeleteCount);
    }

    [Fact]
    public async Task An_empty_source_makes_everything_a_delete()
    {
        var result = await DataComparer.CompareAsync(
            Stream(),
            Stream(("a", "1"), ("b", "2")),
            Settings());

        Assert.Equal(2, result.DeleteCount);
    }

    [Fact]
    public async Task Two_empty_streams_produce_nothing()
    {
        var result = await DataComparer.CompareAsync(Stream(), Stream(), Settings());

        Assert.False(result.HasChanges);
        Assert.Equal(0, result.SameCount);
    }

    // The rule that stops "seeding" from emptying a target.
    [Theory]
    [InlineData(TableDataMode.TopN)]
    [InlineData(TableDataMode.Filter)]
    public async Task A_limited_row_set_never_emits_a_delete(TableDataMode mode)
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1")),
            Stream(("a", "1"), ("y", "8"), ("z", "9")),
            Settings(mode));

        Assert.True(result.DeletesSuppressed);
        Assert.Equal(0, result.DeleteCount);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task A_limited_row_set_still_reports_inserts_and_updates()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "2"), ("b", "1")),
            Stream(("a", "1"), ("z", "9")),
            Settings(TableDataMode.TopN));

        Assert.Equal(1, result.UpdateCount);
        Assert.Equal(1, result.InsertCount);
        Assert.Equal(0, result.DeleteCount);
    }

    [Fact]
    public async Task All_rows_mode_does_emit_deletes()
    {
        var result = await DataComparer.CompareAsync(
            Stream(("a", "1")),
            Stream(("a", "1"), ("z", "9")),
            Settings(TableDataMode.AllRows));

        Assert.False(result.DeletesSuppressed);
        Assert.Equal(1, result.DeleteCount);
    }

    // A single merge pass is only correct on sorted input, so unsorted input has to stop rather than
    // quietly return a wrong answer.
    [Fact]
    public async Task Unsorted_source_input_is_rejected()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataComparer.CompareAsync(Stream(("b", "1"), ("a", "2")), Stream(), Settings()));

        Assert.Contains("source", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsorted_target_input_is_rejected()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataComparer.CompareAsync(Stream(), Stream(("b", "1"), ("a", "2")), Settings()));

        Assert.Contains("target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_key_in_a_stream_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataComparer.CompareAsync(Stream(("a", "1"), ("a", "2")), Stream(), Settings()));
    }
}
