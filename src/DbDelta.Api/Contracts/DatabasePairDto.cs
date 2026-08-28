namespace DbDelta.Api.Contracts;

// Source and Target carry the full per-database rows, so the screen can show sizes and counts without a
// second shape. One of them is null when the database is only on one side.
//
// Signal is deliberately not a boolean: "we did not look", "we could not look" and "the counts agree" are
// three different answers, and only the first two are honest about what was not established.
public sealed record DatabasePairDto(
    string Name,
    string Kind,
    bool CanBeCompared,
    string Signal,
    string Detail,
    InstanceDatabaseDto? Source,
    InstanceDatabaseDto? Target);
