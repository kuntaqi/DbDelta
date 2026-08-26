using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// Three independent aggregates over the same rows. One value can collide; three agreeing by accident
// is vanishingly unlikely, and the count alone already catches most drift.
public sealed record TableFingerprint(ObjectIdentity Table, long RowCount, long Xor, long Sum)
{
    public bool Matches(TableFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return RowCount == other.RowCount && Xor == other.Xor && Sum == other.Sum;
    }
}
