namespace DbDelta.Core.Model;

// Three different things share the name "user-defined type", and only two of them are made of SQL.
public enum UserDefinedTypeKind
{
    // CREATE TYPE x FROM nvarchar(20) — a named base type, used by columns.
    Alias,

    // CREATE TYPE x AS TABLE (…) — used by table-valued parameters.
    Table,

    // A CLR type. Syncing one means syncing the assembly behind it, which this tool does not do, so it is
    // read in order to be reported rather than in order to be emitted.
    Clr
}
