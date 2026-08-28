using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

// The numbers here are what SQL Server actually reports, checked against a server rather than invented:
// COLLATIONPROPERTY(name, 'ComparisonStyle') gives 196609 for CI_AS, 196608 for CS_AS and 0 for BIN2, and
// 'CodePage' gives 1252 for both Latin1_General_CI_AS and SQL_Latin1_General_CP1_CI_AS.
public sealed class CollationPreconditionTests
{
    private const int CiAs = 196609;
    private const int CsAs = 196608;

    private static readonly Dictionary<string, CollationFact> Facts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Latin1_General_CI_AS"] = new("Latin1_General_CI_AS", 1252, CiAs),
        ["SQL_Latin1_General_CP1_CI_AS"] = new("SQL_Latin1_General_CP1_CI_AS", 1252, CiAs),
        ["Latin1_General_CS_AS"] = new("Latin1_General_CS_AS", 1252, CsAs),
        ["Cyrillic_General_CI_AS"] = new("Cyrillic_General_CI_AS", 1251, CiAs)
    };

    private static ColumnDefinition Text(string name, string collation, string type = "varchar") =>
        new()
        {
            Name = name,
            DataType = new DataTypeSpec(type, 50),
            Collation = collation
        };

    private static TableDefinition Table(params ColumnDefinition[] columns) =>
        Build.Table("Company", [Build.Column("CompanyId", "int"), .. columns]);

    private static IReadOnlyList<CollationFinding> Evaluate(
        TableDefinition source,
        TableDefinition target,
        params string[] compared) =>
        CollationPrecondition.Evaluate(source, target, ["CompanyId"], compared, Facts);

    // The pair that made the old warning useless. Two names, one code page, one sensitivity — nothing
    // about a comparison changes, and saying otherwise is what taught people to skip the message.
    [Fact]
    public void Two_names_for_the_same_behaviour_produce_no_finding()
    {
        var source = Table(Text("Name", "Latin1_General_CI_AS"));
        var target = Table(Text("Name", "SQL_Latin1_General_CP1_CI_AS"));

        Assert.Empty(Evaluate(source, target, "Name"));
    }

    // Verified on a server: the same stored byte 0xE0 hashes differently under 1252 and 1251, because the
    // hash converts to nvarchar and the conversion decodes through the column's code page.
    [Fact]
    public void A_different_code_page_on_a_varchar_column_blocks()
    {
        var source = Table(Text("Name", "Latin1_General_CI_AS"));
        var target = Table(Text("Name", "Cyrillic_General_CI_AS"));

        var finding = Assert.Single(Evaluate(source, target, "Name"));

        Assert.Equal(CollationRisk.Blocking, finding.Risk);
        Assert.Equal("Name", finding.Column);
        Assert.Contains("1252", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("1251", finding.Reason, StringComparison.Ordinal);
    }

    // An nvarchar column stores characters, so no code page is consulted and the same difference is
    // harmless. Also verified on a server: the two hashes come back equal.
    [Fact]
    public void The_same_code_page_difference_on_an_nvarchar_column_does_not_block()
    {
        var source = Table(Text("Name", "Latin1_General_CI_AS", "nvarchar"));
        var target = Table(Text("Name", "Cyrillic_General_CI_AS", "nvarchar"));

        Assert.Empty(Evaluate(source, target, "Name"));
    }

    // Sensitivity does not change a hash — values are compared exactly — so on an ordinary column this is
    // worth saying and nothing more.
    [Fact]
    public void Case_sensitivity_on_a_compared_column_is_advisory()
    {
        var source = Table(Text("Name", "Latin1_General_CI_AS"));
        var target = Table(Text("Name", "Latin1_General_CS_AS"));

        var finding = Assert.Single(Evaluate(source, target, "Name"));

        Assert.Equal(CollationRisk.Advisory, finding.Risk);
        Assert.False(CollationPrecondition.Blocks([finding]));
    }

    // On a key column the same difference is fatal: the merge join calls two rows distinct and the target's
    // unique index calls them the same, so the compare reports missing rows and the inserts collide.
    [Fact]
    public void The_same_difference_on_a_key_column_blocks()
    {
        var source = Build.Table("Company", [Text("Code", "Latin1_General_CI_AS"), Build.Column("Name")]);
        var target = Build.Table("Company", [Text("Code", "Latin1_General_CS_AS"), Build.Column("Name")]);

        var findings = CollationPrecondition.Evaluate(source, target, ["Code"], ["Name"], Facts);
        var finding = Assert.Single(findings);

        Assert.Equal(CollationRisk.Blocking, finding.Risk);
        Assert.Contains("same row", finding.Reason, StringComparison.Ordinal);
    }

    // Guessing "probably fine" about a collation nobody can describe is how a silent wrong answer gets out.
    [Fact]
    public void A_collation_the_server_could_not_describe_blocks()
    {
        var source = Table(Text("Name", "Something_Custom_CI_AS"));
        var target = Table(Text("Name", "Latin1_General_CI_AS"));

        var finding = Assert.Single(Evaluate(source, target, "Name"));

        Assert.Equal(CollationRisk.Blocking, finding.Risk);
        Assert.Contains("unknown", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_textual_column_is_not_examined()
    {
        var source = Table(Build.Column("Total", "int"));
        var target = Table(Build.Column("Total", "int"));

        Assert.Empty(Evaluate(source, target, "Total"));
    }

    [Fact]
    public void A_column_missing_from_the_target_is_skipped_rather_than_flagged()
    {
        var source = Table(Text("Name", "Latin1_General_CI_AS"));
        var target = Table();

        Assert.Empty(Evaluate(source, target, "Name"));
    }

    [Fact]
    public void Identical_collations_produce_nothing()
    {
        var table = Table(Text("Name", "Latin1_General_CI_AS"));

        Assert.Empty(Evaluate(table, table, "Name"));
    }

    [Fact]
    public void The_schema_wide_pass_only_covers_tables_on_both_sides()
    {
        var source = Build.Schema("Src", [
            Build.Table("Company", [Build.Column("CompanyId", "int"), Text("Name", "Latin1_General_CI_AS")]),
            Build.Table("Orphan", [Build.Column("Id", "int"), Text("Note", "Latin1_General_CI_AS")])
        ]);

        var target = Build.Schema("Tgt", [
            Build.Table("Company", [Build.Column("CompanyId", "int"), Text("Name", "Cyrillic_General_CI_AS")])
        ]);

        var findings = CollationPrecondition.EvaluateSchema(source, target, Facts);

        Assert.All(findings, f => Assert.Equal("Company", f.Table.Name));
        Assert.True(CollationPrecondition.Blocks(findings));
    }
}
