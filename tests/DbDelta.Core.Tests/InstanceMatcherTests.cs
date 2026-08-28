using DbDelta.Core.Instances;
using DbDelta.Core.Model;

namespace DbDelta.Core.Tests;

public sealed class InstanceMatcherTests
{
    private static DatabaseSummary Db(
        string name,
        bool accessible = true,
        string state = "ONLINE") =>
        new(name, state, "FULL", 0, 0, false, accessible);

    [Fact]
    public void Databases_with_the_same_name_pair_up()
    {
        var match = InstanceMatcher.Match([Db("AppDev")], [Db("AppDev")]);

        var pair = Assert.Single(match.Pairs);

        Assert.Equal(PairKind.ByName, pair.Kind);
        Assert.True(pair.OnBothSides);
        Assert.Equal(1, match.OnBothSides);
        Assert.Empty(match.Problems);
    }

    // Database names are case-insensitive in practice, so treating these as two databases would report one
    // missing on each side.
    [Fact]
    public void Name_matching_ignores_case()
    {
        var match = InstanceMatcher.Match([Db("AppDev")], [Db("APPDEV")]);

        Assert.True(Assert.Single(match.Pairs).OnBothSides);
    }

    [Fact]
    public void What_is_only_on_one_side_is_said_to_be_on_that_side()
    {
        var match = InstanceMatcher.Match([Db("AppDev"), Db("Extra")], [Db("AppDev"), Db("Legacy")]);

        Assert.Equal(1, match.OnBothSides);
        Assert.Equal(1, match.SourceOnly);
        Assert.Equal(1, match.TargetOnly);
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.SourceOnly && p.Name == "Extra");
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.TargetOnly && p.Name == "Legacy");
    }

    // The case the whole feature turns on. Where the environment is part of the name, nothing matches, and
    // the caller has to be able to tell that apart from "the two servers agree".
    [Fact]
    public void Two_servers_whose_names_encode_the_environment_match_nothing_by_name()
    {
        var match = InstanceMatcher.Match([Db("AppProd")], [Db("AppUat")]);

        Assert.Equal(0, match.OnBothSides);
        Assert.Equal(1, match.SourceOnly);
        Assert.Equal(1, match.TargetOnly);
    }

    [Fact]
    public void A_declared_pairing_matches_databases_that_share_no_name()
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd")],
            [Db("AppUat")],
            [new DatabasePairing("AppProd", "AppUat")]);

        var pair = Assert.Single(match.Pairs);

        Assert.Equal(PairKind.Declared, pair.Kind);
        Assert.Equal("AppProd", pair.Source!.Name);
        Assert.Equal("AppUat", pair.Target!.Name);
        Assert.Empty(match.Problems);
    }

    // A declared pairing consumes both names, so a coincidental name on the other side cannot steal one of
    // them back and leave the declared pairing half applied.
    [Fact]
    public void A_declared_pairing_wins_over_a_coincidental_name()
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd"), Db("AppUat")],
            [Db("AppUat")],
            [new DatabasePairing("AppProd", "AppUat")]);

        Assert.Equal(1, match.OnBothSides);
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.Declared && p.Source!.Name == "AppProd");

        // The source's own AppUat now has nothing to pair with, and says so rather than disappearing.
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.SourceOnly && p.Name == "AppUat");
    }

    [Fact]
    public void Declared_pairings_are_matched_case_insensitively()
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd")],
            [Db("AppUat")],
            [new DatabasePairing("appprod", "APPUAT")]);

        Assert.Equal(PairKind.Declared, Assert.Single(match.Pairs).Kind);
    }

    // A pairing that names something absent is a typo. Dropping it silently would make the database that
    // does exist look like it was accounted for.
    [Fact]
    public void A_pairing_naming_a_database_that_is_not_there_is_reported_and_both_sides_still_listed()
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd")],
            [Db("AppUat")],
            [new DatabasePairing("AppProd", "Typo")]);

        Assert.Equal(0, match.OnBothSides);
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.SourceOnly && p.Name == "AppProd");
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.TargetOnly && p.Name == "AppUat");

        var problem = Assert.Single(match.Problems);
        Assert.Contains("Typo is not on the target", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pairing_where_neither_side_exists_says_so_once()
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd")],
            [Db("AppUat")],
            [new DatabasePairing("Nope", "AlsoNope")]);

        Assert.Contains("neither", Assert.Single(match.Problems), StringComparison.Ordinal);
    }

    // One source database cannot correspond to two targets, and keeping the first quietly would drop the
    // second from the comparison.
    [Fact]
    public void Pairing_the_same_source_twice_is_reported()
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd")],
            [Db("AppUat"), Db("AppTest")],
            [new DatabasePairing("AppProd", "AppUat"), new DatabasePairing("AppProd", "AppTest")]);

        Assert.Equal(1, match.OnBothSides);
        Assert.Contains("paired more than once", Assert.Single(match.Problems), StringComparison.Ordinal);
        Assert.Contains(match.Pairs, p => p.Kind == PairKind.TargetOnly && p.Name == "AppTest");
    }

    [Theory]
    [InlineData("", "AppUat")]
    [InlineData("AppProd", "")]
    [InlineData("   ", "   ")]
    public void A_pairing_with_an_empty_side_is_ignored_and_reported(string left, string right)
    {
        var match = InstanceMatcher.Match(
            [Db("AppProd")], [Db("AppUat")], [new DatabasePairing(left, right)]);

        Assert.Contains("empty database", Assert.Single(match.Problems), StringComparison.Ordinal);
    }

    // Being on both sides is not the same as being readable. A database that is offline or closed to this
    // login is unknown, not equal.
    [Theory]
    [InlineData(false, "ONLINE")]
    [InlineData(true, "OFFLINE")]
    [InlineData(true, "RESTORING")]
    public void A_pair_that_cannot_be_read_is_not_reported_as_comparable(bool accessible, string state)
    {
        var match = InstanceMatcher.Match([Db("AppDev")], [Db("AppDev", accessible, state)]);

        var pair = Assert.Single(match.Pairs);

        Assert.True(pair.OnBothSides);
        Assert.False(pair.CanBeCompared);
    }

    [Fact]
    public void A_pair_online_and_accessible_on_both_sides_can_be_compared()
    {
        var match = InstanceMatcher.Match([Db("AppDev")], [Db("AppDev")]);

        Assert.True(Assert.Single(match.Pairs).CanBeCompared);
    }

    // Paired rows first, then source-only, then target-only, and by name inside each group, so two runs
    // against the same servers read the same way.
    [Fact]
    public void The_order_is_stable_and_puts_the_actionable_rows_first()
    {
        var match = InstanceMatcher.Match(
            [Db("Zulu"), Db("Alpha"), Db("OnlySource")],
            [Db("Zulu"), Db("Alpha"), Db("OnlyTarget")]);

        Assert.Equal(
            ["Alpha", "Zulu", "OnlySource", "OnlyTarget"],
            match.Pairs.Select(p => p.Name));
    }

    [Fact]
    public void Two_empty_instances_produce_nothing_rather_than_failing()
    {
        var match = InstanceMatcher.Match([], []);

        Assert.Empty(match.Pairs);
        Assert.Empty(match.Problems);
        Assert.Equal(0, match.OnBothSides);
    }

    [Fact]
    public void A_duplicate_name_on_one_side_is_reported_rather_than_throwing()
    {
        var match = InstanceMatcher.Match([Db("AppDev"), Db("APPDEV")], [Db("AppDev")]);

        Assert.Contains("more than once", Assert.Single(match.Problems), StringComparison.Ordinal);
        Assert.Equal(1, match.OnBothSides);
    }
}
