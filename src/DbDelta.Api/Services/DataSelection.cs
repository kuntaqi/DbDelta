using DbDelta.Core.Data;

namespace DbDelta.Api.Services;

// Rows null means the whole table, which is what picking a table has always meant. A non-null set is a
// narrowing to those rows and nothing else — including the empty set, which is a table narrowed to
// nothing rather than a table restored to everything.
//
// The picks live inside the table's selection rather than beside it, so removing the table from the plan
// takes its picks with it. A parallel dictionary would eventually disagree with this one.
public sealed record DataSelection(
    TableDataMode Mode,
    int TopCount,
    string? Filter,
    IReadOnlySet<string>? Rows = null);
