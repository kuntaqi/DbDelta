using DbDelta.Core.Apply;

namespace DbDelta.Core.Tests;

public sealed class ApplyGuardsTests
{
    private static ApplyGuardContext Context(
        string confirmation = "AppUat",
        bool readOnly = false,
        bool allowDestructive = false,
        params string[] destructive) =>
        new()
        {
            TargetServer = "DBSERVER-UAT",
            TargetDatabase = "AppUat",
            Confirmation = confirmation,
            TargetIsReadOnly = readOnly,
            AllowDestructive = allowDestructive,
            DestructiveSteps = destructive
        };

    [Fact]
    public void A_confirmed_non_destructive_apply_to_a_writable_target_passes()
    {
        Assert.Empty(ApplyGuards.Evaluate(Context()));
    }

    [Fact]
    public void A_read_only_target_is_blocked_even_when_everything_else_is_right()
    {
        var blockers = ApplyGuards.Evaluate(Context(readOnly: true));

        var blocker = Assert.Single(blockers);
        Assert.Contains("read-only list", blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_confirmation_blocks()
    {
        var blockers = ApplyGuards.Evaluate(Context(confirmation: string.Empty));

        Assert.Contains(blockers, b => b.Contains("Type the target database name", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_confirmation_blocks()
    {
        Assert.NotEmpty(ApplyGuards.Evaluate(Context(confirmation: "AppDev")));
    }

    // Typing the name exists to make someone look at which database they are about to change, so a
    // near miss is not close enough.
    [Fact]
    public void Confirmation_is_case_sensitive()
    {
        Assert.NotEmpty(ApplyGuards.Evaluate(Context(confirmation: "appuat")));
    }

    [Fact]
    public void Destructive_steps_block_until_acknowledged()
    {
        var blockers = ApplyGuards.Evaluate(Context(destructive: "drop table dbo.SegmentLegacy"));

        var blocker = Assert.Single(blockers);
        Assert.Contains("drop objects or columns", blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void Acknowledged_destructive_steps_pass()
    {
        Assert.Empty(ApplyGuards.Evaluate(Context(allowDestructive: true, destructive: "drop table dbo.SegmentLegacy")));
    }

    // Reporting one problem at a time turns a refusal into a guessing game.
    [Fact]
    public void Every_failing_guard_is_reported_together()
    {
        var blockers = ApplyGuards.Evaluate(Context(
            confirmation: "wrong",
            readOnly: true,
            destructive: "drop column dbo.Company.Old"));

        Assert.Equal(3, blockers.Count);
    }

    [Fact]
    public void Acknowledging_drops_does_not_unlock_a_read_only_server()
    {
        var blockers = ApplyGuards.Evaluate(Context(
            readOnly: true,
            allowDestructive: true,
            destructive: "drop table dbo.SegmentLegacy"));

        var blocker = Assert.Single(blockers);
        Assert.Contains("read-only list", blocker, StringComparison.Ordinal);
    }
}
