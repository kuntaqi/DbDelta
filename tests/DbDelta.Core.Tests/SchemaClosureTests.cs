using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.Core.Tests;

public sealed class SchemaClosureTests
{
    [Fact]
    public void A_picked_table_pulls_in_the_parent_its_foreign_key_needs()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
                Build.Table("Category")
            ]);

        var closure = Expand(source, Build.Schema("Tgt"), Build.TableId("Company"));

        var added = Assert.Single(closure.Required);
        Assert.Equal("dbo.Category", added.Identity.QualifiedName);
        Assert.Equal("dbo.Company", added.RequiredBy.QualifiedName);
        Assert.Contains("FK_Company_Category", added.Reason);
        Assert.Contains(Build.TableId("Category"), closure.Selection);
    }

    [Fact]
    public void A_parent_the_target_already_has_is_left_alone()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
                Build.Table("Category")
            ]);

        var target = Build.Schema("Tgt", [Build.Table("Category")]);

        var closure = Expand(source, target, Build.TableId("Company"));

        Assert.Empty(closure.Required);
        Assert.Equal([Build.TableId("Company")], closure.Selection);
    }

    [Fact]
    public void Closure_follows_the_chain_rather_than_one_hop()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table("SiteContact", foreignKeys: [Build.ForeignKey("FK_SiteContact_Contact", "ContactId", "Contact")]),
                Build.Table("Contact", foreignKeys: [Build.ForeignKey("FK_Contact_Company", "CompanyId", "Company")]),
                Build.Table("Company")
            ]);

        var closure = Expand(source, Build.Schema("Tgt"), Build.TableId("SiteContact"));

        Assert.Equal(
            ["dbo.Contact", "dbo.Company"],
            closure.Required.Select(r => r.Identity.QualifiedName));
    }

    // The parent exists, so nothing looks missing until the key tries to point at a column that is not
    // there. Pulling the parent in brings the ALTER that adds it.
    [Fact]
    public void A_parent_missing_the_referenced_column_is_pulled_in_even_though_it_exists()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
                Build.Table("Category", columns: [Build.Column("Id", "INT")])
            ]);

        var target = Build.Schema("Tgt", [Build.Table("Category", columns: [Build.Column("Name", "NVARCHAR", 50)])]);

        var closure = Expand(source, target, Build.TableId("Company"));

        var added = Assert.Single(closure.Required);
        Assert.Equal("dbo.Category", added.Identity.QualifiedName);
        Assert.Contains("Id", added.Reason);
    }

    [Fact]
    public void An_altered_table_pulls_in_parents_only_for_the_keys_being_added()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table(
                    "Company",
                    columns: [Build.Column("Id", "INT"), Build.Column("CategoryId", "INT"), Build.Column("RegionId", "INT")],
                    foreignKeys:
                    [
                        Build.ForeignKey("FK_Company_Category", "CategoryId", "Category"),
                        Build.ForeignKey("FK_Company_Region", "RegionId", "Region")
                    ]),
                Build.Table("Category"),
                Build.Table("Region")
            ]);

        // The target already carries one of the two keys, so only the other one needs its parent.
        var target = Build.Schema(
            "Tgt",
            [
                Build.Table(
                    "Company",
                    columns: [Build.Column("Id", "INT"), Build.Column("CategoryId", "INT"), Build.Column("RegionId", "INT")],
                    foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
                Build.Table("Category")
            ]);

        var closure = Expand(source, target, Build.TableId("Company"));

        var added = Assert.Single(closure.Required);
        Assert.Equal("dbo.Region", added.Identity.QualifiedName);
    }

    [Fact]
    public void A_view_pulls_in_the_table_it_reads_from()
    {
        var source = Build.Schema(
            "Src",
            [Build.Table("Company")],
            [Build.View("vCompany", "CREATE VIEW dbo.vCompany AS SELECT Id FROM dbo.Company", dependsOn: [Build.TableId("Company")])]);

        var closure = Expand(source, Build.Schema("Tgt"), new ObjectIdentity(ObjectType.View, "dbo", "vCompany"));

        var added = Assert.Single(closure.Required);
        Assert.Equal("dbo.Company", added.Identity.QualifiedName);
        Assert.Contains("reads from it", added.Reason);
    }

    [Fact]
    public void A_trigger_pulls_in_the_table_it_sits_on()
    {
        var source = Build.Schema(
            "Src",
            [Build.Table("Company")],
            triggers: [Build.Trigger("trg_Company_Audit", "Company")]);

        var closure = Expand(source, Build.Schema("Tgt"), new ObjectIdentity(ObjectType.Trigger, "dbo", "trg_Company_Audit"));

        var added = Assert.Single(closure.Required);
        Assert.Equal("dbo.Company", added.Identity.QualifiedName);
    }

    [Fact]
    public void A_self_referencing_table_requires_nothing()
    {
        var source = Build.Schema(
            "Src",
            [Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Parent", "ParentId", "Company")])]);

        var closure = Expand(source, Build.Schema("Tgt"), Build.TableId("Company"));

        Assert.Empty(closure.Required);
        Assert.Empty(closure.Unsatisfiable);
    }

    // A key pointing at something the source does not have either. Nothing can be emitted for it, so the
    // only useful move is to say so — the apply will fail on that FK and this is the warning of it.
    [Fact]
    public void A_prerequisite_the_comparison_cannot_supply_is_reported_rather_than_dropped()
    {
        var source = Build.Schema(
            "Src",
            [Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Ledger", "LedgerId", "Ledger")])]);

        var closure = Expand(source, Build.Schema("Tgt"), Build.TableId("Company"));

        Assert.Empty(closure.Required);
        Assert.Contains("dbo.Ledger", Assert.Single(closure.Unsatisfiable));
    }

    // Prerequisites are derived, never stored. If they were written into the selection, unticking the one
    // object that wanted them would leave them behind in the plan.
    [Fact]
    public void Nothing_is_required_once_the_object_that_required_it_is_unticked()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
                Build.Table("Category")
            ]);

        var target = Build.Schema("Tgt");
        var diff = new SchemaComparer().Compare(source, target);

        Assert.Single(SchemaClosure.Expand(source, target, diff, [Build.TableId("Company")]).Required);
        Assert.Empty(SchemaClosure.Expand(source, target, diff, []).Required);
    }

    [Fact]
    public void An_object_picked_outright_is_not_reported_as_added_on_your_behalf()
    {
        var source = Build.Schema(
            "Src",
            [
                Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")]),
                Build.Table("Category")
            ]);

        var closure = Expand(source, Build.Schema("Tgt"), Build.TableId("Company"), Build.TableId("Category"));

        Assert.Empty(closure.Required);
        Assert.Equal(2, closure.Selection.Count);
    }

    private static ClosureResult Expand(
        DatabaseSchema source,
        DatabaseSchema target,
        params ObjectIdentity[] selected) =>
        SchemaClosure.Expand(source, target, new SchemaComparer().Compare(source, target), selected);
}
