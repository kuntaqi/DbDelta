namespace DbDelta.Core.Model;

// A view or function created WITH SCHEMABINDING that reads this table. The binding is what matters: the
// server refuses to alter or drop anything such a module names until the module itself is gone, so a
// column it reads cannot be altered in place and the table cannot be rebuilt. Columns is empty when the
// module names the table without naming a column of it — COUNT(*) is enough to hold the table.
public sealed record SchemaBoundReference(ObjectIdentity Module, IReadOnlyList<string> Columns);
