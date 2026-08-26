namespace DbDelta.Api.Services;

// A key the schema already states without calling it the primary key: a UNIQUE constraint, or a unique
// index over NOT NULL columns.
public sealed record DeclaredKey(string Name, IReadOnlyList<string> Columns);
