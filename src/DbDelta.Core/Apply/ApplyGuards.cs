namespace DbDelta.Core.Apply;

// Domain rules rather than HTTP plumbing, so they are unit tested directly instead of only through
// a live endpoint. Every blocker is returned, not just the first: telling someone one problem at a
// time turns a refusal into a guessing game.
public static class ApplyGuards
{
    public static IReadOnlyList<string> Evaluate(ApplyGuardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var blockers = new List<string>();

        if (context.TargetIsReadOnly)
        {
            blockers.Add(
                $"{context.TargetServer} is on the read-only list. Download the script and run it yourself.");
        }

        // Ordinal, and deliberately case sensitive: the point of typing the name is to make the person
        // look at which database they are about to change.
        if (!string.Equals(context.Confirmation, context.TargetDatabase, StringComparison.Ordinal))
        {
            blockers.Add($"Type the target database name exactly ({context.TargetDatabase}) to confirm.");
        }

        // Drops are the irreversible part of a schema sync, so they need their own consent rather than
        // riding along with everything else that was ticked.
        if (context.DestructiveSteps.Count > 0 && !context.AllowDestructive)
        {
            blockers.Add(
                $"{context.DestructiveSteps.Count} step(s) drop objects or columns. Acknowledge them explicitly to continue.");
        }

        return blockers;
    }
}
