using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// Footprint: how big the object already is. Context for judgement, not the consequence of a sync.
public sealed record TableVolume(ObjectIdentity Table, long RowCount, long DataBytes, long IndexBytes)
{
    public long TotalBytes => DataBytes + IndexBytes;
}
