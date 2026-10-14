namespace DbDelta.Core.Model;

// Several different things share the name "user-defined type" across engines, and only some are made of SQL.
public enum UserDefinedTypeKind
{
    // A named base type with an optional size/nullability. SQL Server: CREATE TYPE x FROM nvarchar(20).
    // PostgreSQL: a domain (CREATE DOMAIN x AS …).
    Alias,

    // A named row shape. SQL Server: CREATE TYPE x AS TABLE (…), used by table-valued parameters.
    // PostgreSQL: a composite type (CREATE TYPE x AS (…)).
    Table,

    // A CLR type. Syncing one means syncing the assembly behind it, which this tool does not do, so it is
    // read in order to be reported rather than in order to be emitted. SQL Server only.
    Clr,

    // A named set of ordered text labels. PostgreSQL: CREATE TYPE x AS ENUM (…). The labels are the whole of
    // the type, so EnumLabels carries them and nothing else distinguishes two enums.
    Enum
}
