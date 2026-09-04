using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

// A renamed view or routine keeps its old name inside its own body forever, because sp_rename does not
// touch sys.sql_modules. The emitter now writes the catalog's name instead of the body's, which is only
// half a fix: the source body still says the old name, so unless the comparison stops reading names out of
// bodies, a correctly created target object reports as Different on every compare afterwards and the
// apply never converges. These are the tests for that second half.
public sealed class RenamedProgrammableTests
{
    private readonly SchemaComparer _comparer = new();

    private static readonly ObjectIdentity Category = new(ObjectType.View, "dbo", "vCategory");

    private static DatabaseSchema WithView(string database, string body) =>
        Build.Schema(database, views: [Build.View("vCategory", body)]);

    [Fact]
    public void A_source_that_was_renamed_matches_a_target_created_under_the_catalog_name()
    {
        var source = WithView("Src", "CREATE VIEW dbo.vCategoryOld AS SELECT CategoryId FROM dbo.Category");
        var target = WithView("Tgt", "CREATE VIEW [dbo].[vCategory] AS SELECT CategoryId FROM dbo.Category");

        var diff = _comparer.Compare(source, target);

        Assert.Empty(diff.Differing);
        Assert.Equal(DiffKind.Same, diff.Find(Category)!.Kind);
    }

    // The same shape the other way round: the target is the renamed one. Nothing needs deploying either
    // way, because the object the target holds behaves exactly like the source's.
    [Fact]
    public void A_target_that_was_renamed_matches_a_source_created_under_the_catalog_name()
    {
        var source = WithView("Src", "CREATE VIEW dbo.vCategory AS SELECT CategoryId FROM dbo.Category");
        var target = WithView("Tgt", "CREATE VIEW dbo.vSomethingElse AS SELECT CategoryId FROM dbo.Category");

        Assert.Empty(_comparer.Compare(source, target).Differing);
    }

    [Fact]
    public void A_body_that_actually_differs_is_still_reported()
    {
        var source = WithView("Src", "CREATE VIEW dbo.vCategoryOld AS SELECT CategoryId FROM dbo.Category");
        var target = WithView("Tgt", "CREATE VIEW dbo.vCategory AS SELECT CategoryId, Name FROM dbo.Category");

        var view = _comparer.Compare(source, target).Find(Category);

        Assert.Equal(DiffKind.Different, view!.Kind);

        // And what is shown is what the databases hold, not the rewritten text the comparison worked on.
        var definition = Assert.Single(view.Properties, p => p.Property == "Definition");
        Assert.Contains("vCategoryOld", definition.Source, StringComparison.Ordinal);
        Assert.Contains("CategoryId, Name", definition.Target, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renamed_routine_converges_the_same_way()
    {
        var identity = new ObjectIdentity(ObjectType.Routine, "dbo", "usp_GetCategory");

        DatabaseSchema Schema(string database, string body) => new()
        {
            DatabaseName = database,
            Routines =
            [
                new RoutineDefinition
                {
                    Identity = identity,
                    Kind = RoutineKind.Procedure,
                    Definition = body
                }
            ]
        };

        var diff = _comparer.Compare(
            Schema("Src", "CREATE PROCEDURE dbo.usp_GetCategoryOld AS SELECT 1"),
            Schema("Tgt", "CREATE PROCEDURE [dbo].[usp_GetCategory] AS SELECT 1"));

        Assert.Empty(diff.Differing);
    }

    // Not the rename case, but the same false difference and it is fixed by the same rewrite: one side
    // written by hand, the other by this tool, which brackets everything it emits.
    [Fact]
    public void Brackets_around_the_name_are_not_a_difference()
    {
        var source = WithView("Src", "CREATE VIEW dbo.vCategory AS SELECT 1 AS x");
        var target = WithView("Tgt", "CREATE VIEW [dbo].[vCategory] AS SELECT 1 AS x");

        Assert.Empty(_comparer.Compare(source, target).Differing);
    }

    [Fact]
    public void A_renamed_trigger_converges_too()
    {
        var identity = new ObjectIdentity(ObjectType.Trigger, "dbo", "trgCategoryAudit");

        DatabaseSchema Schema(string database, string body) => new()
        {
            DatabaseName = database,
            Triggers =
            [
                new TriggerDefinition
                {
                    Identity = identity,
                    Table = new ObjectIdentity(ObjectType.Table, "dbo", "Category"),
                    Definition = body
                }
            ]
        };

        var diff = _comparer.Compare(
            Schema("Src", "CREATE TRIGGER dbo.trgAuditOld ON dbo.Category AFTER INSERT AS SELECT 1"),
            Schema("Tgt", "CREATE TRIGGER [dbo].[trgCategoryAudit] ON dbo.Category AFTER INSERT AS SELECT 1"));

        Assert.Empty(diff.Differing);
    }
}
