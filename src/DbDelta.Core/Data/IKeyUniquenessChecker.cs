using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// The merge join classifies rows by key and rejects a repeated key outright. Verifying uniqueness before
// accepting a hand-picked key turns a confusing mid-compare failure into a clear refusal up front.
public interface IKeyUniquenessChecker
{
    Task<KeyUniqueness> CheckAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken = default);
}
