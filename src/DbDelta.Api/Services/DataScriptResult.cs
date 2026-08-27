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

    public static DataScriptResult Empty { get; } = new()
    {
        Steps = [],
        DeleteWarnings = [],
        RequiredRows = [],
        ClosureWarnings = []
    };
}
