using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Tests;

// Against a real temporary directory, because what this class does is keep a file.
public sealed class RecentPairStoreTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "DbDeltaRecentTests", Guid.NewGuid().ToString("n"));

    private RecentPairStore Store() =>
        new(Options.Create(new StorageOptions { DataDirectory = _scratch }));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static ComparedEndpoint End(string server, string database) =>
        new(server, null, database, "Windows", null, true);

    [Fact]
    public async Task Nothing_recorded_yet_is_an_empty_list_rather_than_a_failure()
    {
        Assert.Empty(await Store().ListAsync());
    }

    [Fact]
    public async Task A_recorded_pair_comes_back_with_both_sides()
    {
        var store = Store();
        await store.RecordAsync(End("DBSERVER-PROD", "AppProd"), End("DBSERVER-UAT", "AppUat"));

        var pair = Assert.Single(await store.ListAsync());

        Assert.Equal("DBSERVER-PROD", pair.Source.Server);
        Assert.Equal("AppProd", pair.Source.Database);
        Assert.Equal("DBSERVER-UAT", pair.Target.Server);
        Assert.Equal("AppUat", pair.Target.Database);
    }

    [Fact]
    public async Task The_most_recent_pair_is_first()
    {
        var store = Store();
        await store.RecordAsync(End("A", "One"), End("B", "One"));
        await store.RecordAsync(End("A", "Two"), End("B", "Two"));

        var pairs = await store.ListAsync();

        Assert.Equal(2, pairs.Count);
        Assert.Equal("Two", pairs[0].Source.Database);
    }

    // Comparing the same pair again is not a second pair. It moves to the top, because the list is about
    // what to offer next rather than a history of attempts.
    [Fact]
    public async Task The_same_pair_again_moves_up_rather_than_appearing_twice()
    {
        var store = Store();
        await store.RecordAsync(End("A", "One"), End("B", "One"));
        await store.RecordAsync(End("A", "Two"), End("B", "Two"));
        await store.RecordAsync(End("A", "One"), End("B", "One"));

        var pairs = await store.ListAsync();

        Assert.Equal(2, pairs.Count);
        Assert.Equal("One", pairs[0].Source.Database);
    }

    // Database and server names are case-insensitive in practice, so the same pair typed differently is
    // still the same pair.
    [Fact]
    public async Task Identity_ignores_case()
    {
        var store = Store();
        await store.RecordAsync(End("dbserver-prod", "appprod"), End("dbserver-uat", "appuat"));
        await store.RecordAsync(End("DBSERVER-PROD", "AppProd"), End("DBSERVER-UAT", "AppUat"));

        Assert.Single(await store.ListAsync());
    }

    // A pair is directional: comparing prod to uat is not the same act as comparing uat to prod, and
    // offering one when the other was meant would point a sync the wrong way.
    [Fact]
    public async Task Swapping_the_sides_is_a_different_pair()
    {
        var store = Store();
        await store.RecordAsync(End("A", "One"), End("B", "Two"));
        await store.RecordAsync(End("B", "Two"), End("A", "One"));

        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task The_list_is_capped_and_drops_the_oldest()
    {
        var store = Store();

        for (var i = 0; i < RecentPairStore.Keep + 5; i++)
        {
            await store.RecordAsync(End("A", $"Db{i}"), End("B", $"Db{i}"));
        }

        var pairs = await store.ListAsync();

        Assert.Equal(RecentPairStore.Keep, pairs.Count);
        Assert.Equal($"Db{RecentPairStore.Keep + 4}", pairs[0].Source.Database);
        Assert.DoesNotContain(pairs, p => p.Source.Database == "Db0");
    }

    [Fact]
    public async Task Forgetting_empties_the_list()
    {
        var store = Store();
        await store.RecordAsync(End("A", "One"), End("B", "One"));

        Assert.Empty(await store.ForgetAsync());
        Assert.Empty(await store.ListAsync());
    }

    // The rule the shape is supposed to hold. A password cannot be in the file because ComparedEndpoint has
    // nowhere to put one — this asserts it against the serialized text rather than field by field, so it
    // keeps holding as fields are added.
    [Fact]
    public async Task A_pasted_connection_string_is_taken_apart_and_its_password_is_not_written_down()
    {
        var store = Store();

        var endpoint = ConnectionEndpoint.From(new ConnectionRequest(
            ConnectionString: "Server=DBSERVER-UAT,1433;Database=AppUat;User ID=deploy;Password=hunter2;TrustServerCertificate=true"));

        await store.RecordAsync(endpoint, End("B", "One"));

        var text = await File.ReadAllTextAsync(Path.Combine(_scratch, "recent.json"));

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("DBSERVER-UAT", text, StringComparison.Ordinal);
        Assert.Contains("deploy", text, StringComparison.Ordinal);

        var pair = Assert.Single(await store.ListAsync());
        Assert.Equal(1433, pair.Source.Port);
        Assert.Equal("SqlLogin", pair.Source.Authentication);
    }

    // A convenience must never be the reason a comparison fails, so a file that cannot be parsed reads as
    // "nothing remembered" rather than throwing into the compare that just succeeded.
    [Fact]
    public async Task A_corrupt_file_reads_as_nothing_remembered()
    {
        Directory.CreateDirectory(_scratch);
        await File.WriteAllTextAsync(Path.Combine(_scratch, "recent.json"), "{ not json at all");

        Assert.Empty(await Store().ListAsync());
    }

    // And it recovers: the next successful compare writes a good file over the bad one.
    [Fact]
    public async Task Recording_over_a_corrupt_file_recovers()
    {
        Directory.CreateDirectory(_scratch);
        await File.WriteAllTextAsync(Path.Combine(_scratch, "recent.json"), "{ not json at all");

        var store = Store();
        await store.RecordAsync(End("A", "One"), End("B", "One"));

        Assert.Single(await store.ListAsync());
    }
}
