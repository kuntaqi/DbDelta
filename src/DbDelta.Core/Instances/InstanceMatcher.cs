using DbDelta.Core.Model;

namespace DbDelta.Core.Instances;

// Lines two servers' database lists up against each other. This is the cheap half of comparing instances:
// two queries, no connection per database, and it answers the only question that can be answered that
// cheaply — which databases exist on both sides.
//
// Matching by name is the default and is not sufficient on its own. Where the environment is part of the
// name — `AppProd` against `AppUat`, which is this project's own example convention — the two servers share
// no names, so name matching alone would report every database as present on one side and missing on the
// other. That is indistinguishable from "these instances have nothing to do with each other", and it is the
// reason declared pairings exist and the reason the caller is told how many pairs were actually found.
//
// Declared pairings are applied first and consume both names, so a pairing cannot be undone by a name that
// happens to coincide.
public static class InstanceMatcher
{
    public static InstanceMatch Match(
        IReadOnlyList<DatabaseSummary> source,
        IReadOnlyList<DatabaseSummary> target,
        IEnumerable<DatabasePairing>? declared = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var sourceByName = Index(source, "source", out var sourceProblems);
        var targetByName = Index(target, "target", out var targetProblems);

        var problems = new List<string>(sourceProblems);
        problems.AddRange(targetProblems);

        var pairs = new List<DatabasePair>();
        var takenSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pairing in declared ?? [])
        {
            if (string.IsNullOrWhiteSpace(pairing.Source) || string.IsNullOrWhiteSpace(pairing.Target))
            {
                problems.Add("A pairing named an empty database and was ignored.");
                continue;
            }

            // A source database cannot correspond to two targets, and quietly keeping one of them would
            // drop the other from the comparison entirely.
            if (!claimed.Add(pairing.Source))
            {
                problems.Add(
                    $"{pairing.Source} was paired more than once. Only the first pairing was used.");
                continue;
            }

            var haveSource = sourceByName.TryGetValue(pairing.Source, out var left);
            var haveTarget = targetByName.TryGetValue(pairing.Target, out var right);

            if (!haveSource || !haveTarget)
            {
                var missing = !haveSource && !haveTarget
                    ? $"neither {pairing.Source} on the source nor {pairing.Target} on the target exists"
                    : !haveSource
                        ? $"{pairing.Source} is not on the source"
                        : $"{pairing.Target} is not on the target";

                problems.Add(
                    $"The pairing {pairing.Source} -> {pairing.Target} was not applied: {missing}. Whatever "
                    + "does exist is listed on its own side instead.");

                continue;
            }

            pairs.Add(new DatabasePair(left, right, PairKind.Declared));
            takenSource.Add(left!.Name);
            takenTarget.Add(right!.Name);
        }

        foreach (var left in source.Where(d => !takenSource.Contains(d.Name)))
        {
            if (targetByName.TryGetValue(left.Name, out var right) && !takenTarget.Contains(right!.Name))
            {
                pairs.Add(new DatabasePair(left, right, PairKind.ByName));
                takenSource.Add(left.Name);
                takenTarget.Add(right.Name);
            }
        }

        pairs.AddRange(source
            .Where(d => !takenSource.Contains(d.Name))
            .Select(d => new DatabasePair(d, null, PairKind.SourceOnly)));

        pairs.AddRange(target
            .Where(d => !takenTarget.Contains(d.Name))
            .Select(d => new DatabasePair(null, d, PairKind.TargetOnly)));

        return new InstanceMatch
        {
            // Paired first, because those are the rows anyone can act on, then the one-sided ones. Within
            // each group by name, so two runs against the same servers read the same way.
            Pairs = pairs
                .OrderBy(p => p.OnBothSides ? 0 : 1)
                .ThenBy(p => p.Kind == PairKind.TargetOnly ? 1 : 0)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Problems = problems
        };
    }

    // A server cannot really hold two databases whose names differ only by case, but the list arrives as
    // data rather than as a guarantee, and a duplicate key would throw here rather than be reported.
    private static Dictionary<string, DatabaseSummary> Index(
        IReadOnlyList<DatabaseSummary> databases,
        string side,
        out List<string> problems)
    {
        var index = new Dictionary<string, DatabaseSummary>(StringComparer.OrdinalIgnoreCase);
        problems = [];

        foreach (var database in databases)
        {
            if (!index.TryAdd(database.Name, database))
            {
                problems.Add(
                    $"The {side} lists {database.Name} more than once, ignoring case. Only the first was used.");
            }
        }

        return index;
    }
}
