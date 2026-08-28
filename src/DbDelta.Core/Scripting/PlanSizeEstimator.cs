namespace DbDelta.Core.Scripting;

// How large a plan's script will be, answered before the script is built.
//
// The existing check answered the same question afterwards: assemble everything, count the string, and set
// a flag. That is fine for reporting and useless for deciding, because by then the expensive part — reading
// every changed row out of both databases — has already happened. A plan of fifty tables is minutes of
// fetching before anyone is told the result is too big to read.
//
// The two halves are not equally knowable, and this does not pretend they are:
//
//   The schema half is exact. Emitting DDL is string building over schemas already in memory, with no
//   database access at all, so the caller can just build it and measure it. It is also the small half —
//   390 objects came to 152 KB against a real database.
//
//   The data half is a range. What decides its size is how many rows differ, and nothing short of the
//   two-pass compare knows that. The scan says *whether* a table differs; the volume reader says how many
//   rows it has. So the count sits between "at least the difference in row counts" and "at most the whole
//   table", and both ends are reported rather than one being picked and dressed up as the answer.
//
// The saving grace is staging. Past a threshold a table's rows leave the script entirely and go over the
// wire into a staging table, so each table can contribute at most that threshold however many rows it
// holds. That is what keeps the upper bound tight enough to be worth printing.
public static class PlanSizeEstimator
{
    // Stored bytes per row are the only cheap proxy for how wide the same row is written as SQL, and they
    // are a loose one in both directions. A number written as text is wider than its binary form; unicode
    // text is narrower, because the page holds two bytes per character and the script holds roughly one;
    // and the measurement carries page slack, which overstates narrow rows. Against the demo data the SQL
    // came to about 0.7 of the stored width — one calibration point, not a law.
    //
    // So the width is a band rather than a factor, and the band composes with the row range instead of
    // replacing it: the floor takes the fewest rows at the narrowest plausible width, the ceiling takes the
    // most rows at the widest. Composing only the row uncertainty is what made a first version of this
    // report a floor of 210 KB for a script that came out at 81 KB — a minimum above the truth, which is a
    // false statement rather than a cautious one.
    private const int WidthUncertainty = 2;

    // What a staged table leaves behind in the script: the staging table's DDL, the bulk load reference,
    // the merge and the cleanup. Fixed regardless of row count, which is the whole point of staging.
    private const long StagedBytes = 2048;

    public static PlanSizeEstimate Estimate(
        IEnumerable<TableSizeInput> tables,
        long schemaBytes,
        long maxInlineTableBytes,
        long maxReviewableBytes)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var minBytes = 0L;
        var maxBytes = 0L;
        var minRows = 0L;
        var maxRows = 0L;
        var counted = 0;
        var notScanned = 0;
        var staged = 0;
        var mayStage = 0;

        foreach (var table in tables)
        {
            counted++;

            var (low, high) = Rows(table, ref notScanned);
            var perRow = Math.Max(1, table.BytesPerRow);

            minRows += low;
            maxRows += high;

            // Both uncertainties at once: fewest rows at the narrowest width, most rows at the widest.
            var lowBytes = low * Math.Max(1, perRow / WidthUncertainty);
            var highBytes = high * perRow * WidthUncertainty;

            // Certain to stage: even the smallest this table could be is past the threshold, so its rows
            // are not going into the script at all.
            if (lowBytes > maxInlineTableBytes)
            {
                staged++;
                minBytes += StagedBytes;
                maxBytes += StagedBytes;
                continue;
            }

            // Might stage. The worst case for *script size* is that it turns out just small enough to stay
            // inline, so the threshold is the upper bound — not the unbounded row estimate.
            if (highBytes > maxInlineTableBytes)
            {
                mayStage++;
                minBytes += lowBytes;
                maxBytes += maxInlineTableBytes;
                continue;
            }

            minBytes += lowBytes;
            maxBytes += highBytes;
        }

        var notes = new List<string>();

        if (notScanned > 0)
        {
            notes.Add(
                $"{notScanned} picked table(s) have not been scanned, so nothing is known about whether they "
                + "differ. Their upper bound assumes every row is written and their lower bound assumes none "
                + "is. Scanning them narrows this more than anything else here.");
        }

        if (staged > 0)
        {
            notes.Add(
                $"{staged} table(s) are certain to take the staged path, so their rows leave the script "
                + "entirely and cost it a fixed amount each however many rows move.");
        }

        if (mayStage > 0)
        {
            notes.Add(
                $"{mayStage} table(s) may take the staged path depending on how many rows differ. Their "
                + "upper bound is the staging threshold rather than their whole row count, because past it "
                + "the rows stop being part of the script.");
        }

        notes.Add(
            "The schema half is measured, not estimated. The data half is a range for two reasons at once: "
            + "how many rows differ is unknown until they are compared, and how wide a row is as SQL is only "
            + "approximated from how wide it is on disk — within about a factor of two either way. The range "
            + "is the two together, so the real figure should land inside it rather than near either end.");

        return new PlanSizeEstimate(
            schemaBytes,
            minBytes,
            maxBytes,
            minRows,
            maxRows,
            counted,
            notScanned,
            Verdict(schemaBytes + minBytes, schemaBytes + maxBytes, maxReviewableBytes),
            notes);
    }

    private static (long Low, long High) Rows(TableSizeInput table, ref int notScanned)
    {
        // Narrowed to particular rows: the count is the pick, and there is nothing to estimate.
        if (table.ExactRows is { } exact)
        {
            return (exact, exact);
        }

        var ceiling = Math.Max(table.SourceRows, table.TargetRows);

        if (!table.Scanned)
        {
            notScanned++;
            return (0, ceiling);
        }

        // Scanned and identical is the one cheap certainty in here: no rows move, so no bytes.
        if (!table.Differs)
        {
            return (0, 0);
        }

        // It differs, so at least one row moves — and at least as many as the row counts disagree by, since
        // that many have to be inserted or deleted before the two sides can hold the same number.
        return (Math.Max(1, Math.Abs(table.SourceRows - table.TargetRows)), ceiling);
    }

    private static ReviewableVerdict Verdict(long min, long max, long limit) =>
        min > limit ? ReviewableVerdict.Exceeds
            : max > limit ? ReviewableVerdict.Possibly
            : ReviewableVerdict.Within;
}
