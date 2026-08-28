using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Services;

// Profiles remember one connection each, which still leaves two of them to choose every time — and the
// thing anyone actually repeats is a *pair*. This remembers the pairs that were compared, so the common
// case is one click instead of two selections.
//
// Only successful comparisons are recorded. A pair that could not connect is not worth offering back, and
// recording failures would fill the list with the attempts someone was in the middle of correcting.
//
// No passwords, held the same way profiles hold it: ComparedEndpoint has no field for one. Under a SQL
// login, restoring a pair fills everything except the password, which is asked for each session.
public sealed class RecentPairStore
{
    // Enough to cover the pairs someone moves between, short enough that the list stays scannable and the
    // file stays small. Beyond this the oldest goes.
    public const int Keep = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public RecentPairStore(IOptions<StorageOptions> storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _path = Path.Combine(storage.Value.Resolve(), "recent.json");
    }

    public async Task<IReadOnlyList<ComparedPair>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var pairs = JsonSerializer.Deserialize<List<ComparedPair>>(text) ?? [];

            // Ordered on read rather than trusted from the file: a hand-edited or half-written file should
            // still come back in a sensible order rather than in whatever order it happens to hold.
            return pairs.OrderByDescending(p => p.At).Take(Keep).ToList();
        }
        catch (JsonException)
        {
            // A corrupt file is a convenience lost, not a failure worth propagating into a compare.
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    // Called after a comparison succeeds. It must never be the reason a compare fails, so anything that
    // goes wrong writing it is swallowed — the list is a convenience and the comparison already worked.
    public async Task RecordAsync(
        ComparedEndpoint source,
        ComparedEndpoint target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var entry = new ComparedPair(source, target, DateTimeOffset.Now);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await ListAsync(cancellationToken).ConfigureAwait(false);

            // The same pair compared again moves to the top rather than appearing twice. Its auth details
            // are taken from the new entry, since that is the one that just worked.
            var kept = existing
                .Where(p => !string.Equals(p.Key, entry.Key, StringComparison.Ordinal))
                .ToList();

            kept.Insert(0, entry);

            await File.WriteAllTextAsync(
                _path,
                JsonSerializer.Serialize(kept.Take(Keep).ToList(), Json),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ComparedPair>> ForgetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            _gate.Release();
        }

        return [];
    }
}
