namespace DbDelta.Core.Data;

public sealed class DataComparer
{
    // Both sides arrive ordered by the same canonical key text under a binary collation, which is
    // what lets this walk the two streams once and hold nothing but the current row from each.
    // Ordinal comparison here has to match that server-side ordering exactly or the merge desynchronises.
    public static async Task<DataCompareResult> CompareAsync(
        IAsyncEnumerable<KeyHashRow> source,
        IAsyncEnumerable<KeyHashRow> target,
        DataCompareSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);

        var differences = new List<RowDifference>();
        var same = 0;

        await using var left = source.GetAsyncEnumerator(cancellationToken);
        await using var right = target.GetAsyncEnumerator(cancellationToken);

        var hasLeft = await left.MoveNextAsync().ConfigureAwait(false);
        var hasRight = await right.MoveNextAsync().ConfigureAwait(false);

        // Each side is checked only when it actually advances. Checking the current row every
        // iteration would flag the side that stood still as a duplicate of itself.
        async ValueTask<bool> AdvanceLeft()
        {
            var consumed = left.Current.Key;
            var moved = await left.MoveNextAsync().ConfigureAwait(false);
            if (moved)
            {
                Guard(consumed, left.Current.Key, "source");
            }

            return moved;
        }

        async ValueTask<bool> AdvanceRight()
        {
            var consumed = right.Current.Key;
            var moved = await right.MoveNextAsync().ConfigureAwait(false);
            if (moved)
            {
                Guard(consumed, right.Current.Key, "target");
            }

            return moved;
        }

        while (hasLeft || hasRight)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var order = !hasRight ? -1
                : !hasLeft ? 1
                : string.CompareOrdinal(left.Current.Key, right.Current.Key);

            if (order < 0)
            {
                differences.Add(new RowDifference(left.Current.Key, RowClassification.Insert));
                hasLeft = await AdvanceLeft().ConfigureAwait(false);
            }
            else if (order > 0)
            {
                // Absence from a limited row set means "outside the limit", not "deleted". Emitting a
                // delete here would empty the target while claiming to seed it.
                if (!settings.SuppressDeletes)
                {
                    differences.Add(new RowDifference(right.Current.Key, RowClassification.Delete));
                }

                hasRight = await AdvanceRight().ConfigureAwait(false);
            }
            else
            {
                if (string.Equals(left.Current.Hash, right.Current.Hash, StringComparison.Ordinal))
                {
                    same++;
                }
                else
                {
                    differences.Add(new RowDifference(left.Current.Key, RowClassification.Update));
                }

                hasLeft = await AdvanceLeft().ConfigureAwait(false);
                hasRight = await AdvanceRight().ConfigureAwait(false);
            }
        }

        return new DataCompareResult
        {
            Differences = differences,
            SameCount = same,
            DeletesSuppressed = settings.SuppressDeletes,
            ExcludedColumns = settings.ExcludedColumns,
            ComparedColumns = settings.ComparedColumns
        };
    }

    // A single merge pass is only correct on sorted input. Silently returning nonsense when the
    // server's ordering disagrees with ordinal comparison would be far worse than stopping.
    private static void Guard(string? previous, string current, string side)
    {
        if (previous is not null && string.CompareOrdinal(previous, current) >= 0)
        {
            throw new InvalidOperationException(
                $"The {side} stream is not in ascending key order ('{previous}' then '{current}'). "
                + "The key query must order by the canonical key under a binary collation.");
        }
    }
}
