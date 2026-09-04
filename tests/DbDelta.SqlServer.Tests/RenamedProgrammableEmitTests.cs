using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer.Tests;

// sp_rename leaves the old name inside the body, so emitting the stored text verbatim creates the old name
// on the target — an object nobody asked for, reported as a successful apply. The name comes from the
// catalog instead.
//
// Schemas in memory, no LocalDB: what is under test is which name ends up in the emitted text, and no
// server is needed to answer that. The end-to-end proof that the apply then converges lives in
// EndToEndCompareTests, where there is a real database to rename an object in.
public sealed class RenamedProgrammableEmitTests
{
    private static DatabaseSchema WithView(string database, string name, string body) =>
        new()
        {
            DatabaseName = database,
            Views = [new ViewDefinition { Identity = new ObjectIdentity(ObjectType.View, "dbo", name), Definition = body }]
        };

    private static ScriptStep Programmable(DatabaseSchema source)
    {
        var target = new DatabaseSchema { DatabaseName = "Tgt" };
        var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

        return Assert.Single(script.Steps, s => s.Phase == ScriptPhase.Programmables);
    }

    [Fact]
    public void The_catalog_name_is_emitted_and_not_the_one_the_body_carries()
    {
        var step = Programmable(WithView("Src", "vCategory", "CREATE VIEW dbo.vCategoryOld AS SELECT 1 AS x"));

        Assert.Contains("CREATE OR ALTER VIEW [dbo].[vCategory]", step.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vCategoryOld", step.Sql, StringComparison.Ordinal);
    }

    // Silence here is the whole defect: the apply reports success either way, so the step has to say that
    // the name it ran is not the name the body asked for.
    [Fact]
    public void The_step_says_the_name_came_from_the_catalog()
    {
        var step = Programmable(WithView("Src", "vCategory", "CREATE VIEW dbo.vCategoryOld AS SELECT 1 AS x"));

        Assert.Contains("still creates it as dbo.vCategoryOld", step.Description, StringComparison.Ordinal);
        Assert.Contains("from the catalog", step.Description, StringComparison.Ordinal);
    }

    // The same rewrite fixes a different hazard: an unqualified CREATE lands in the default schema of
    // whoever runs the script, which need not be the schema the object is in on the source.
    [Fact]
    public void An_unqualified_header_is_given_its_schema()
    {
        var step = Programmable(WithView("Src", "vCategory", "CREATE VIEW vCategory AS SELECT 1 AS x"));

        Assert.Contains("CREATE OR ALTER VIEW [dbo].[vCategory]", step.Sql, StringComparison.Ordinal);
        Assert.Contains("unqualified", step.Description, StringComparison.Ordinal);
    }

    // Nothing is rewritten when nothing disagrees, so the text an ordinary object emits is untouched by
    // any of this — including the brackets it does not have.
    [Fact]
    public void An_object_whose_body_already_names_it_is_emitted_unchanged()
    {
        var step = Programmable(WithView("Src", "vCategory", "CREATE VIEW dbo.vCategory AS SELECT 1 AS x"));

        Assert.Contains("CREATE OR ALTER VIEW dbo.vCategory AS SELECT 1 AS x", step.Sql, StringComparison.Ordinal);
        Assert.Equal("view dbo.vCategory", step.Description);
    }

    // A definition whose CREATE cannot be read is emitted as it was stored — the behaviour before any of
    // this existed — but it no longer passes for an ordinary step.
    [Fact]
    public void A_header_it_cannot_read_is_emitted_as_stored_and_flagged()
    {
        var step = Programmable(WithView("Src", "vCategory", "ALTER VIEW dbo.vCategory AS SELECT 1 AS x"));

        Assert.Contains("ALTER VIEW dbo.vCategory", step.Sql, StringComparison.Ordinal);
        Assert.Contains("was not recognised", step.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renamed_trigger_keeps_the_table_it_sits_on()
    {
        var source = new DatabaseSchema
        {
            DatabaseName = "Src",
            Triggers =
            [
                new TriggerDefinition
                {
                    Identity = new ObjectIdentity(ObjectType.Trigger, "dbo", "trgCategoryAudit"),
                    Table = new ObjectIdentity(ObjectType.Table, "dbo", "Category"),
                    Definition = "CREATE TRIGGER dbo.trgAuditOld ON dbo.Category AFTER INSERT AS SELECT 1"
                }
            ]
        };

        var step = Programmable(source);

        Assert.Contains("CREATE OR ALTER TRIGGER [dbo].[trgCategoryAudit] ON dbo.Category", step.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renamed_procedure_is_emitted_under_the_catalog_name()
    {
        var source = new DatabaseSchema
        {
            DatabaseName = "Src",
            Routines =
            [
                new RoutineDefinition
                {
                    Identity = new ObjectIdentity(ObjectType.Routine, "dbo", "usp_GetCategory"),
                    Kind = RoutineKind.Procedure,
                    Definition = "CREATE PROCEDURE dbo.usp_GetCategoryOld AS SELECT 1"
                }
            ]
        };

        var step = Programmable(source);

        Assert.Contains("CREATE OR ALTER PROCEDURE [dbo].[usp_GetCategory]", step.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("usp_GetCategoryOld", step.Sql, StringComparison.Ordinal);
    }
}
