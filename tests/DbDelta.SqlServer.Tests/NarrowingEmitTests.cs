using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer.Tests;

// A narrowing column change is destructive even though it drops nothing: the server rounds DECIMAL scale
// and DATETIME2 precision without complaint, so an unflagged step rewrites existing rows and reports
// success.
//
// Built from schemas in memory rather than from LocalDB. Two earlier attempts got this wrong. The first
// created its table in the *shared* source database, which broke EndToEndCompareTests — that database is
// read-only by contract, as the fixture says in as many words. The second gave each test its own scratch
// pair, which was correct but paid six CREATE DATABASE round trips for a question that is decided by two
// type specs and never touches a server. There is nothing to read here, so reading nothing is right.
public sealed class NarrowingEmitTests
{
    private static ColumnDefinition Col(string name, DataTypeSpec type, int ordinal) =>
        new() { Name = name, DataType = type, IsNullable = true, OrdinalPosition = ordinal };

    private static DatabaseSchema Schema(string name, DataTypeSpec label, DataTypeSpec amount) =>
        new()
        {
            DatabaseName = name,
            Tables =
            [
                new TableDefinition
                {
                    Identity = new ObjectIdentity(ObjectType.Table, "dbo", "Priced"),
                    Columns =
                    [
                        Col("Id", new DataTypeSpec("int"), 1),
                        Col("Label", label, 2),
                        Col("Amount", amount, 3)
                    ]
                }
            ]
        };

    private static readonly DataTypeSpec NarrowLabel = new("nvarchar", MaxLength: 20);
    private static readonly DataTypeSpec WideLabel = new("nvarchar", MaxLength: 80);
    private static readonly DataTypeSpec NarrowAmount = new("decimal", Precision: 18, Scale: 2);
    private static readonly DataTypeSpec WideAmount = new("decimal", Precision: 18, Scale: 4);

    private static List<ScriptStep> Alters(DataTypeSpec sourceLabel, DataTypeSpec sourceAmount, DataTypeSpec targetLabel, DataTypeSpec targetAmount)
    {
        var source = Schema("Src", sourceLabel, sourceAmount);
        var target = Schema("Tgt", targetLabel, targetAmount);
        var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

        return script.Steps
            .Where(s => s.Phase == ScriptPhase.AlterColumns && s.Sql.Contains("[Priced]"))
            .ToList();
    }

    [Fact]
    public void A_narrowing_column_change_is_marked_destructive_and_says_why()
    {
        var alters = Alters(NarrowLabel, NarrowAmount, WideLabel, WideAmount);

        Assert.Equal(2, alters.Count);
        Assert.All(alters, s => Assert.True(s.Destructive, $"not marked destructive: {s.Description}"));

        var amount = Assert.Single(alters, s => s.Sql.Contains("[Amount]"));
        Assert.Contains("rounded to 2 decimal place(s)", amount.Description);
        Assert.Contains("does not refuse", amount.Description);

        var label = Assert.Single(alters, s => s.Sql.Contains("[Label]"));
        Assert.Contains("longer than 20 characters", label.Description);
    }

    [Fact]
    public void Widening_the_same_columns_is_not_destructive()
    {
        // The mirror image, so the flag cannot be "every ALTER COLUMN is destructive".
        var alters = Alters(WideLabel, WideAmount, NarrowLabel, NarrowAmount);

        Assert.Equal(2, alters.Count);
        Assert.All(alters, s => Assert.False(s.Destructive, $"wrongly marked destructive: {s.Description}"));
    }

    [Fact]
    public void The_apply_is_blocked_until_the_rewrite_is_acknowledged()
    {
        var destructive = Alters(NarrowLabel, NarrowAmount, WideLabel, WideAmount)
            .Where(s => s.Destructive)
            .Select(s => s.Description)
            .ToList();

        Assert.NotEmpty(destructive);

        ApplyGuardContext Context(bool allow) => new()
        {
            TargetServer = "DBSERVER-DEV",
            TargetIsReadOnly = false,
            TargetDatabase = "AppDev",
            Confirmation = "AppDev",
            AllowDestructive = allow,
            DestructiveSteps = destructive
        };

        Assert.Contains(ApplyGuards.Evaluate(Context(false)), b => b.Contains("rewrites data already there"));
        Assert.Empty(ApplyGuards.Evaluate(Context(true)));
    }
}
