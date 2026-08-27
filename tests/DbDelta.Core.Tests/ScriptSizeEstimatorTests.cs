using DbDelta.Core.Data;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Tests;

public sealed class ScriptSizeEstimatorTests
{
    [Fact]
    public void An_empty_table_estimates_near_nothing()
    {
        Assert.True(ScriptSizeEstimator.EstimateBytes(Table()) < 256);
    }

    // Linear in rows, once the per-table constant is taken off both sides. Comparing the totals instead
    // would just be measuring the constant on a table this small.
    [Fact]
    public void The_estimate_grows_in_step_with_the_rows()
    {
        var empty = ScriptSizeEstimator.EstimateBytes(Table());
        var one = ScriptSizeEstimator.EstimateBytes(Table(Insert("1")));
        var three = ScriptSizeEstimator.EstimateBytes(Table(Insert("1"), Insert("2"), Insert("3")));

        Assert.Equal((one - empty) * 3, three - empty);
    }

    // An update repeats every column name as an assignment, so it costs more per row than an insert of
    // the same values. A delete writes only its key, which is why deletes stay inline.
    [Fact]
    public void An_update_costs_more_than_an_insert_and_a_delete_costs_least()
    {
        var insert = ScriptSizeEstimator.EstimateBytes(Table(Insert("1")));
        var update = ScriptSizeEstimator.EstimateBytes(Table(Update("1")));
        var delete = ScriptSizeEstimator.EstimateBytes(Table(Delete("1")));

        Assert.True(update > insert, $"update {update} vs insert {insert}");
        Assert.True(delete < insert, $"delete {delete} vs insert {insert}");
    }

    [Fact]
    public void A_long_value_costs_more_than_a_short_one()
    {
        var small = ScriptSizeEstimator.EstimateBytes(Table(Insert("1", "x")));
        var large = ScriptSizeEstimator.EstimateBytes(Table(Insert("1", new string('x', 4000))));

        Assert.True(large - small > 3900, $"{large} vs {small}");
    }

    private static DataChange Insert(string key, string value = "value") =>
        new(key, key, RowClassification.Insert, Values(key, value), Keys(key));

    private static DataChange Update(string key, string value = "value") =>
        new(key, key, RowClassification.Update, Values(key, value), Keys(key), "hash");

    private static DataChange Delete(string key) =>
        new(key, key, RowClassification.Delete, new Dictionary<string, string?>(), Keys(key), "hash");

    private static IReadOnlyDictionary<string, string?> Values(string key, string value) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = key,
            ["Name"] = value
        };

    private static IReadOnlyDictionary<string, string?> Keys(string key) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Id"] = key };

    private static TableDataChanges Table(params DataChange[] changes) =>
        new()
        {
            Table = Build.Table("Company", columns: [Build.Column("Id", "INT"), Build.Column("Name")]),
            KeyColumns = ["Id"],
            Columns = ["Name"],
            Changes = changes
        };
}
