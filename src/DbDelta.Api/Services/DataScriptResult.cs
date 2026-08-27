using DbDelta.Core.Data;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

public sealed class DataScriptResult
{
    public required IReadOnlyList<ScriptStep> Steps { get; init; }

    // Over-limit delete shares. Kept apart from the closure warnings because these need acknowledging
    // before an apply, where a closure warning is something to read.
    public required IReadOnlyList<string> DeleteWarnings { get; init; }

    public required IReadOnlyList<RequiredRows> RequiredRows { get; init; }

    public required IReadOnlyList<string> ClosureWarnings { get; init; }

    // What the target held, for the rows this plan writes, at the moment it was built. Compared against
    // the same thing at apply time, this is what catches a row that moved in between.
    public required RowStateSnapshot Rows { get; init; }

    public static DataScriptResult Empty { get; } = new()
    {
        Steps = [],
        DeleteWarnings = [],
        RequiredRows = [],
        ClosureWarnings = [],
        Rows = RowStateSnapshot.From([])
    };
}
