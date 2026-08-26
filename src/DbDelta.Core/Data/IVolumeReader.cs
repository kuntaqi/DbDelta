namespace DbDelta.Core.Data;

public interface IVolumeReader
{
    Task<DatabaseVolume> ReadAsync(CancellationToken cancellationToken = default);
}
