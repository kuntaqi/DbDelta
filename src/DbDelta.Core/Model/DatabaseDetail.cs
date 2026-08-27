namespace DbDelta.Core.Model;

// What costs a connection. Object counts are per-database by nature, and collation turns out to be too:
// sys.databases.collation_name reads NULL on instances where DATABASEPROPERTYEX inside the database
// answers correctly, so the reliable source is a query run while connected to it.
//
// Problem carries why a database could not be read instead of dropping it from the list. A database that
// refused is a fact about the instance, and the survey exists to report facts about the instance.
public sealed record DatabaseDetail(
    string Name,
    string? Collation,
    int Tables,
    int Views,
    int Routines,
    string? Problem);
