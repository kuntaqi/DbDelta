using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

public sealed class SchemaComparerTests
{
    private readonly SchemaComparer _comparer = new();

    [Fact]
    public void Identical_schemas_report_no_changes()
    {
        var source = Build.Schema("Src", [Build.Table("Company")]);
        var target = Build.Schema("Tgt", [Build.Table("Company")]);

        var diff = _comparer.Compare(source, target);

        Assert.Empty(diff.Differing);
        Assert.Equal(1, diff.Count(DiffKind.Same));
    }

    [Fact]
    public void Table_only_in_source_is_source_only()
    {
        var source = Build.Schema("Src", [Build.Table("Company"), Build.Table("CompanyRating")]);
        var target = Build.Schema("Tgt", [Build.Table("Company")]);

        var diff = _comparer.Compare(source, target);

        var rating = diff.Find(Build.TableId("CompanyRating"));
        Assert.NotNull(rating);
        Assert.Equal(DiffKind.SourceOnly, rating.Kind);
    }

    [Fact]
    public void Table_only_in_target_is_target_only()
    {
        var source = Build.Schema("Src", [Build.Table("Company")]);
        var target = Build.Schema("Tgt", [Build.Table("Company"), Build.Table("SegmentLegacy")]);

        var diff = _comparer.Compare(source, target);

        var legacy = diff.Find(Build.TableId("SegmentLegacy"));
        Assert.NotNull(legacy);
        Assert.Equal(DiffKind.TargetOnly, legacy.Kind);
    }

    [Fact]
    public void Widened_column_reports_a_datatype_property_diff()
    {
        var source = Build.Schema("Src", [Build.Table("Company", [Build.Column("Segment", "NVARCHAR", 40)])]);
        var target = Build.Schema("Tgt", [Build.Table("Company", [Build.Column("Segment", "NVARCHAR", 20)])]);

        var diff = _comparer.Compare(source, target);

        var table = diff.Find(Build.TableId("Company"));
        Assert.NotNull(table);
        Assert.Equal(DiffKind.Different, table.Kind);

        var column = Assert.Single(table.DifferingChildren);
        var property = Assert.Single(column.Properties);
        Assert.Equal("DataType", property.Property);
        Assert.Equal("NVARCHAR(40)", property.Source);
        Assert.Equal("NVARCHAR(20)", property.Target);
    }

    [Fact]
    public void Column_missing_on_target_is_reported_as_a_source_only_child()
    {
        var source = Build.Schema("Src", [
            Build.Table("Company", [Build.Column("Id", "INT"), Build.Column("RatingBand", "TINYINT")])
        ]);
        var target = Build.Schema("Tgt", [Build.Table("Company", [Build.Column("Id", "INT")])]);

        var diff = _comparer.Compare(source, target);

        var table = diff.Find(Build.TableId("Company"));
        Assert.NotNull(table);
        var child = Assert.Single(table.DifferingChildren);
        Assert.Equal(DiffKind.SourceOnly, child.Kind);
        Assert.Equal("RatingBand", child.Identity.Name);
        Assert.Equal(ObjectType.Column, child.Identity.Type);
    }

    [Fact]
    public void Nullability_change_is_detected()
    {
        var source = Build.Schema("Src", [Build.Table("Site", [Build.Column("Category", nullable: true)])]);
        var target = Build.Schema("Tgt", [Build.Table("Site", [Build.Column("Category", nullable: false)])]);

        var diff = _comparer.Compare(source, target);

        var table = diff.Find(Build.TableId("Site"));
        Assert.NotNull(table);
        var column = Assert.Single(table.DifferingChildren);
        Assert.Contains(column.Properties, p => p.Property == "Nullable");
    }

    [Fact]
    public void Object_names_match_case_insensitively()
    {
        var source = Build.Schema("Src", [Build.Table("Company")]);
        var target = Build.Schema("Tgt", [Build.Table("COMPANY")]);

        var diff = _comparer.Compare(source, target);

        Assert.Single(diff.Objects);
        Assert.Equal(DiffKind.Same, diff.Objects[0].Kind);
    }

    [Fact]
    public void Whitespace_only_body_change_is_ignored_by_default()
    {
        var source = Build.Schema("Src", views: [Build.View("vCompanySegment", "SELECT a,\n  b FROM t")]);
        var target = Build.Schema("Tgt", views: [Build.View("vCompanySegment", "SELECT   a, b\nFROM t")]);

        var diff = _comparer.Compare(source, target);

        Assert.Empty(diff.Differing);
    }

    [Fact]
    public void Whitespace_only_body_change_is_reported_when_the_option_is_off()
    {
        var comparer = new SchemaComparer(new ComparisonOptions { IgnoreWhitespaceInBodies = false });
        var source = Build.Schema("Src", views: [Build.View("vCompanySegment", "SELECT a, b FROM t")]);
        var target = Build.Schema("Tgt", views: [Build.View("vCompanySegment", "SELECT  a, b FROM t")]);

        var diff = comparer.Compare(source, target);

        Assert.Single(diff.Differing);
    }

    [Fact]
    public void Real_body_change_is_reported()
    {
        var source = Build.Schema("Src", views: [Build.View("vCompanySegment", "SELECT a FROM t")]);
        var target = Build.Schema("Tgt", views: [Build.View("vCompanySegment", "SELECT b FROM t")]);

        var diff = _comparer.Compare(source, target);

        var view = Assert.Single(diff.Differing);
        Assert.Equal("Definition", Assert.Single(view.Properties).Property);
    }

    [Fact]
    public void Ignored_schemas_are_skipped_entirely()
    {
        var options = new ComparisonOptions
        {
            IgnoredSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audit" }
        };
        var comparer = new SchemaComparer(options);

        var source = Build.Schema("Src", [
            Build.Table("Company"),
            Build.Table("Trail", schema: "audit")
        ]);
        var target = Build.Schema("Tgt", [Build.Table("Company")]);

        var diff = comparer.Compare(source, target);

        Assert.Single(diff.Objects);
        Assert.Equal("Company", diff.Objects[0].Identity.Name);
    }

    [Fact]
    public void Foreign_key_retarget_is_detected()
    {
        var source = Build.Schema("Src", [
            Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "Category")])
        ]);
        var target = Build.Schema("Tgt", [
            Build.Table("Company", foreignKeys: [Build.ForeignKey("FK_Company_Category", "CategoryId", "OldCategory")])
        ]);

        var diff = _comparer.Compare(source, target);

        var table = diff.Find(Build.TableId("Company"));
        Assert.NotNull(table);
        var fk = Assert.Single(table.DifferingChildren);
        Assert.Equal(ObjectType.ForeignKey, fk.Identity.Type);
        Assert.Contains(fk.Properties, p => p.Property == "References");
    }
}
