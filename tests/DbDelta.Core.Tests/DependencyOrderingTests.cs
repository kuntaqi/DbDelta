using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.Core.Tests;

public sealed class DependencyOrderingTests
{
    [Fact]
    public void Parents_are_ordered_before_the_tables_that_reference_them()
    {
        var tables = new[]
        {
            Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
            Build.Table("Category")
        };

        var order = TableDependencyGraph.Build(tables).OrderForData();

        Assert.False(order.HasCycles);
        Assert.Equal(
            ["dbo.Category", "dbo.Company"],
            order.Ordered.Select(t => t.QualifiedName));
    }

    [Fact]
    public void A_three_level_chain_is_ordered_root_first()
    {
        var tables = new[]
        {
            Build.Table("SiteContact", foreignKeys: [Build.ForeignKey("FK_SiteContact_Contact", "ContactId", "Contact")]),
            Build.Table("Contact", foreignKeys: [Build.ForeignKey("FK_Contact_Company", "CompanyId", "Company")]),
            Build.Table("Company")
        };

        var order = TableDependencyGraph.Build(tables).OrderForData();

        Assert.Equal(
            ["dbo.Company", "dbo.Contact", "dbo.SiteContact"],
            order.Ordered.Select(t => t.QualifiedName));
    }

    [Fact]
    public void A_self_referencing_table_is_not_treated_as_a_cycle()
    {
        var tables = new[]
        {
            Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Parent", "ParentId", "Company")])
        };

        var order = TableDependencyGraph.Build(tables).OrderForData();

        Assert.False(order.HasCycles);
        Assert.Single(order.Ordered);
    }

    [Fact]
    public void A_cycle_is_reported_rather_than_thrown()
    {
        var tables = new[]
        {
            Build.Table("A", foreignKeys: [Build.ForeignKey("FK_A_B", "BId", "B")]),
            Build.Table("B", foreignKeys: [Build.ForeignKey("FK_B_A", "AId", "A")])
        };

        var order = TableDependencyGraph.Build(tables).OrderForData();

        Assert.True(order.HasCycles);
        Assert.Equal(2, order.Cyclic.Count);
        Assert.Empty(order.Ordered);
    }

    [Fact]
    public void A_foreign_key_to_a_table_outside_the_set_is_ignored()
    {
        var tables = new[]
        {
            Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Missing", "X", "NotCompared")])
        };

        var order = TableDependencyGraph.Build(tables).OrderForData();

        Assert.False(order.HasCycles);
        Assert.Single(order.Ordered);
    }

    [Fact]
    public void Parent_closure_pulls_parents_in_but_not_children()
    {
        var tables = new[]
        {
            Build.Table("Contact", foreignKeys:
            [
                Build.ForeignKey("FK_Contact_ContactType", "ContactTypeId", "ContactType"),
                Build.ForeignKey("FK_Contact_Company", "CompanyId", "Company")
            ]),
            Build.Table("ContactType"),
            Build.Table("Company"),
            Build.Table("ContactNote", foreignKeys: [Build.ForeignKey("FK_Note_Contact", "ContactId", "Contact")])
        };

        var graph = TableDependencyGraph.Build(tables);
        var closure = graph.ParentClosure([Build.TableId("Contact")]);

        Assert.Contains(Build.TableId("Contact"), closure);
        Assert.Contains(Build.TableId("ContactType"), closure);
        Assert.Contains(Build.TableId("Company"), closure);
        Assert.DoesNotContain(Build.TableId("ContactNote"), closure);
    }

    [Fact]
    public void Children_of_a_table_are_discoverable_for_break_analysis()
    {
        var tables = new[]
        {
            Build.Table("Contact"),
            Build.Table("SiteContact", foreignKeys: [Build.ForeignKey("FK_SiteContact_Contact", "ContactId", "Contact")])
        };

        var graph = TableDependencyGraph.Build(tables);

        var child = Assert.Single(graph.ChildrenOf(Build.TableId("Contact")));
        Assert.Equal("dbo.SiteContact", child.QualifiedName);
    }

    [Fact]
    public void Sorting_is_stable_across_runs()
    {
        var nodes = new[] { "d", "c", "b", "a" };
        var first = TopologicalSorter.Sort(nodes, _ => []);
        var second = TopologicalSorter.Sort(nodes, _ => []);

        Assert.Equal(first.Ordered, second.Ordered);
        Assert.Equal(nodes, first.Ordered);
    }

    [Fact]
    public void Independent_nodes_keep_their_input_order()
    {
        var nodes = new[] { "z", "y", "x" };

        var order = TopologicalSorter.Sort(nodes, _ => []);

        Assert.Equal(["z", "y", "x"], order.Ordered);
    }
}
