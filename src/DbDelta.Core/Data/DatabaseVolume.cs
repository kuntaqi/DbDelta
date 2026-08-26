namespace DbDelta.Core.Data;

public sealed class DatabaseVolume
{
    public required long DataBytes { get; init; }

    public required long LogBytes { get; init; }

    public required long TotalRows { get; init; }

    public IReadOnlyList<TableVolume> Tables { get; init; } = [];
}
