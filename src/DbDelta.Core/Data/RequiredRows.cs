using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// Rows in the plan that nobody picked: the parents a picked row points at. Named per foreign key rather
// than per table, because "3 rows of dbo.Category" says nothing about why they are there.
public sealed record RequiredRows(
    ObjectIdentity Table,
    int RowCount,
    ObjectIdentity RequiredBy,
    string ForeignKeyName);
