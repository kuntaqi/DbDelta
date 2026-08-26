using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

public sealed class ColumnSetResolverTests
{
    private static DataCompareRequest Request(params string[] ignored) => new()
    {
        Table = Build.TableId("Company"),
        KeyColumns = ["CompanyId"],
        IgnoredColumns = new HashSet<string>(ignored, StringComparer.OrdinalIgnoreCase)
    };

    private static TableDefinition Company(params ColumnDefinition[] columns) =>
        Build.Table("Company", columns);

    [Fact]
    public void Key_columns_are_not_part_of_the_compared_set()
    {
        var table = Company(Build.Column("CompanyId", "INT"), Build.Column("Name"));

        var resolution = ColumnSetResolver.Resolve(table, table, Request());

        Assert.Equal(["Name"], resolution.ComparedColumns);
    }

    [Fact]
    public void Only_the_intersection_is_compared_and_the_rest_is_explained()
    {
        var source = Company(Build.Column("CompanyId", "INT"), Build.Column("Name"), Build.Column("Segment"));
        var target = Company(Build.Column("CompanyId", "INT"), Build.Column("Name"), Build.Column("Legacy"));

        var resolution = ColumnSetResolver.Resolve(source, target, Request());

        Assert.Equal(["Name"], resolution.ComparedColumns);
        Assert.Contains(resolution.ExcludedColumns, e => e.Column == "Segment" && e.Reason == "only on source");
        Assert.Contains(resolution.ExcludedColumns, e => e.Column == "Legacy" && e.Reason == "only on target");
    }

    [Fact]
    public void A_rowversion_column_is_excluded_as_not_comparable()
    {
        var table = Company(
            Build.Column("CompanyId", "INT"),
            Build.Column("Name"),
            Build.Column("RowVersion", "timestamp"));

        var resolution = ColumnSetResolver.Resolve(table, table, Request());

        Assert.DoesNotContain("RowVersion", resolution.ComparedColumns);
        Assert.Contains(resolution.ExcludedColumns, e => e.Column == "RowVersion" && e.Reason == "not comparable");
    }

    [Fact]
    public void A_computed_column_is_excluded()
    {
        var computed = new ColumnDefinition
        {
            Name = "FullName",
            DataType = new DataTypeSpec("NVARCHAR", 100),
            ComputedExpression = "([First] + [Last])"
        };
        var table = Company(Build.Column("CompanyId", "INT"), Build.Column("Name"), computed);

        var resolution = ColumnSetResolver.Resolve(table, table, Request());

        Assert.Contains(resolution.ExcludedColumns, e => e.Column == "FullName" && e.Reason == "computed");
    }

    [Fact]
    public void A_column_ignored_by_rule_is_excluded_with_that_reason()
    {
        var table = Company(Build.Column("CompanyId", "INT"), Build.Column("Name"), Build.Column("ModifiedUtc"));

        var resolution = ColumnSetResolver.Resolve(table, table, Request("ModifiedUtc"));

        Assert.Equal(["Name"], resolution.ComparedColumns);
        Assert.Contains(resolution.ExcludedColumns, e => e.Column == "ModifiedUtc" && e.Reason == "excluded by rule");
    }

    [Fact]
    public void The_summary_counts_against_the_full_source_column_list()
    {
        var table = Company(Build.Column("CompanyId", "INT"), Build.Column("Name"), Build.Column("Segment"));

        var resolution = ColumnSetResolver.Resolve(table, table, Request("Segment"));

        Assert.Equal("1 of 3 columns", resolution.Summary);
        Assert.True(resolution.CanCompare);
    }

    [Fact]
    public void A_table_with_a_primary_key_yields_it_as_the_default_key()
    {
        var table = new TableDefinition
        {
            Identity = Build.TableId("Company"),
            Columns = [Build.Column("CompanyId", "INT")],
            PrimaryKey = new PrimaryKeyDefinition { Name = "PK_Company", Columns = [new IndexColumn("CompanyId")] }
        };

        Assert.Equal(["CompanyId"], ColumnSetResolver.DefaultKeyFor(table));
    }

    // Falling back to "all columns are the key" looks like it works until duplicate rows make it wrong,
    // so a keyless table yields nothing and the caller has to ask.
    [Fact]
    public void A_table_without_a_primary_key_yields_no_key()
    {
        Assert.Empty(ColumnSetResolver.DefaultKeyFor(Build.Table("AuditTrail")));
    }
}
