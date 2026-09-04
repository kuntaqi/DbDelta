using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

// The kind an index is decides which statement recreates it, so two indexes that agree on everything else
// and differ in kind are not the same index. The report that started this asked whether a columnstore and
// a rowstore over the same columns would compare as identical once the kind was discarded, and flagged it
// as unverified — the answer, measured against a real server, is that they would not have, but only by
// accident: a columnstore reports its columns as *included* columns, so the two never lined up. The kind
// is compared now so it does not rest on that.
public sealed class IndexKindCompareTests
{
    private readonly SchemaComparer _comparer = new();

    private static DatabaseSchema WithIndex(string database, IndexDefinition index) =>
        Build.Schema(database, [Build.Table("Fact", indexes: [index])]);

    private static IndexDefinition Index(IndexKind kind) =>
        new()
        {
            Name = "IX_Fact",
            Kind = kind,
            Columns = [new IndexColumn("Id", false)]
        };

    [Fact]
    public void Two_indexes_that_differ_only_in_kind_are_different()
    {
        var diff = _comparer.Compare(
            WithIndex("Src", Index(IndexKind.Columnstore)),
            WithIndex("Tgt", Index(IndexKind.Rowstore)));

        var index = Assert.Single(
            Assert.Single(diff.Differing).DifferingChildren,
            c => c.Identity.Type == ObjectType.Index);

        var kind = Assert.Single(index.Properties, p => p.Property == "Kind");
        Assert.Equal("Columnstore", kind.Source);
        Assert.Equal("Rowstore", kind.Target);
    }

    [Fact]
    public void The_same_kind_on_both_sides_is_not_a_difference()
    {
        var diff = _comparer.Compare(
            WithIndex("Src", Index(IndexKind.Spatial)),
            WithIndex("Tgt", Index(IndexKind.Spatial)));

        Assert.Empty(diff.Differing);
    }
}
