using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

public sealed record FingerprintRequest(
    TableDefinition Table,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> Columns)
{
    public string Identity() => Table.Identity.QualifiedName;
}
