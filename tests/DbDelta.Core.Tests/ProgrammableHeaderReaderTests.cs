using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

// The name inside a stored CREATE is the only place a rename shows up: sp_rename updates the catalog and
// leaves sys.sql_modules.definition saying CREATE ... <old name> forever. Everything here is about finding
// that name exactly, because the emitter rewrites the span this reports — landing one character out would
// corrupt a body rather than fix a name.
public sealed class ProgrammableHeaderReaderTests
{
    private static ProgrammableHeader Read(string definition) =>
        Assert.IsType<ProgrammableHeader>(ProgrammableHeaderReader.Read(definition));

    [Theory]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS x")]
    [InlineData("CREATE VIEW [dbo].[v] AS SELECT 1 AS x")]
    [InlineData("CREATE view \"dbo\".\"v\" AS SELECT 1 AS x")]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS x")]
    public void The_schema_and_name_are_read_off_the_header(string definition)
    {
        var header = Read(definition);

        Assert.Equal("dbo", header.Schema);
        Assert.Equal("v", header.Name);
        Assert.True(header.Names(new ObjectIdentity(ObjectType.View, "dbo", "v")));
    }

    // What SQL Server actually stores for an object created with CREATE OR ALTER: the OR ALTER is blanked
    // out and the gap left behind.
    [Fact]
    public void The_blanked_or_alter_a_server_stores_is_still_readable()
    {
        var header = Read("CREATE          VIEW dbo.v AS SELECT 1 AS x");

        Assert.Equal("dbo", header.Schema);
        Assert.Equal("v", header.Name);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("CREATE PROC dbo.p AS SELECT 1")]
    [InlineData("CREATE FUNCTION dbo.p(@a INT) RETURNS INT AS BEGIN RETURN 1 END")]
    [InlineData("CREATE PROCEDURE dbo.p;1 AS SELECT 1")]
    public void Every_programmable_keyword_is_recognised(string definition)
    {
        Assert.Equal("p", Read(definition).Name);
    }

    // A trigger names two objects and only the first is its own.
    [Fact]
    public void A_trigger_reports_its_own_name_and_not_the_table_it_sits_on()
    {
        var header = Read("CREATE TRIGGER [dbo].[trgAudit] ON [dbo].[Category] AFTER INSERT AS SELECT 1");

        Assert.Equal("trgAudit", header.Name);
        Assert.Equal(
            "CREATE TRIGGER [dbo].[trgKeep] ON [dbo].[Category] AFTER INSERT AS SELECT 1",
            header.ReplaceIn(
                "CREATE TRIGGER [dbo].[trgAudit] ON [dbo].[Category] AFTER INSERT AS SELECT 1",
                "[dbo].[trgKeep]"));
    }

    [Fact]
    public void Leading_comments_do_not_hide_the_header()
    {
        var definition = "/****** Object:  View [dbo].[vOld] ******/\n-- and a note\nCREATE VIEW dbo.vOld AS SELECT 1";

        var header = Read(definition);

        Assert.Equal("vOld", header.Name);

        // The comment above it names the old object too, and is left exactly as it was: it is prose the
        // source database holds, not a statement that creates anything.
        Assert.Equal(
            "/****** Object:  View [dbo].[vOld] ******/\n-- and a note\nCREATE VIEW [dbo].[vNew] AS SELECT 1",
            header.ReplaceIn(definition, "[dbo].[vNew]"));
    }

    [Fact]
    public void Whitespace_and_comments_inside_the_name_are_part_of_the_span()
    {
        var definition = "CREATE VIEW dbo . /* here */ [v] AS SELECT 1";

        Assert.Equal("v", Read(definition).Name);
        Assert.Equal("CREATE VIEW [dbo].[v] AS SELECT 1", Read(definition).ReplaceIn(definition, "[dbo].[v]"));
    }

    [Fact]
    public void A_doubled_bracket_is_one_character_of_the_name()
    {
        Assert.Equal("weird]name", Read("CREATE VIEW [dbo].[weird]]name] AS SELECT 1").Name);
    }

    [Fact]
    public void An_unqualified_header_reports_no_schema_and_does_not_name_the_object()
    {
        var header = Read("CREATE VIEW v AS SELECT 1");

        Assert.Null(header.Schema);
        Assert.Equal("v", header.Name);

        // Not a quibble: without a schema, which one the CREATE lands in depends on the default schema of
        // whoever runs the script, so the target can end up with the object somewhere else entirely.
        Assert.False(header.Names(new ObjectIdentity(ObjectType.View, "dbo", "v")));
    }

    [Fact]
    public void A_renamed_object_does_not_name_itself()
    {
        Assert.False(Read("CREATE VIEW dbo.vCategoryOld AS SELECT 1")
            .Names(new ObjectIdentity(ObjectType.View, "dbo", "vCategory")));
    }

    // ObjectIdentity is case-insensitive, so a case-only difference pairs the two objects anyway and is
    // not a rename to correct.
    [Fact]
    public void Case_alone_is_not_a_different_name()
    {
        Assert.True(Read("CREATE VIEW DBO.VCATEGORY AS SELECT 1")
            .Names(new ObjectIdentity(ObjectType.View, "dbo", "vCategory")));
    }

    [Theory]
    [InlineData("ALTER VIEW dbo.v AS SELECT 'CREATE VIEW dbo.x' AS x")]
    [InlineData("CREATE TABLE dbo.t (a INT)")]
    [InlineData("CREATE VIEW [dbo].[unterminated AS SELECT 1")]
    [InlineData("CREATE VIEW dbo. AS SELECT 1")]
    [InlineData("CREATE OR VIEW dbo.v AS SELECT 1")]
    [InlineData("CREATE VIEW")]
    [InlineData("-- only a comment")]
    [InlineData("")]
    public void A_shape_it_does_not_understand_comes_back_as_nothing(string definition)
    {
        Assert.Null(ProgrammableHeaderReader.Read(definition));
    }

    [Fact]
    public void Canonicalising_makes_a_renamed_body_and_a_correct_one_the_same_text()
    {
        var identity = new ObjectIdentity(ObjectType.View, "dbo", "vCategory");

        Assert.Equal(
            ProgrammableHeaderReader.WithCanonicalName("CREATE VIEW dbo.vCategoryOld AS SELECT 1", identity),
            ProgrammableHeaderReader.WithCanonicalName("CREATE VIEW [dbo].[vCategory] AS SELECT 1", identity));
    }

    [Fact]
    public void Canonicalising_leaves_a_body_it_cannot_read_alone()
    {
        const string definition = "ALTER VIEW dbo.v AS SELECT 1";

        Assert.Equal(
            definition,
            ProgrammableHeaderReader.WithCanonicalName(definition, new ObjectIdentity(ObjectType.View, "dbo", "v")));
    }

    // Canonicalising touches the name and nothing else, so a real difference in the body survives it.
    [Fact]
    public void Canonicalising_does_not_make_two_different_bodies_the_same()
    {
        var identity = new ObjectIdentity(ObjectType.View, "dbo", "v");

        Assert.NotEqual(
            ProgrammableHeaderReader.WithCanonicalName("CREATE VIEW dbo.v AS SELECT 1 AS x", identity),
            ProgrammableHeaderReader.WithCanonicalName("CREATE VIEW dbo.v AS SELECT 2 AS x", identity));
    }
}
