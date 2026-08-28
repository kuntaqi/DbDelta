using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Services;

// Runs are written to the user's profile, never to a database. The tool does not create tables in
// anything it connects to, so pointing it at a database can never leave a trace behind.
public sealed class RunLogStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _directory;

    public RunLogStore(IOptions<StorageOptions> storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        _directory = Path.Combine(storage.Value.Resolve(), "runs");
        Directory.CreateDirectory(_directory);
    }

    public string Directory_ => _directory;

    public async Task WriteAsync(RunLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var name = $"{entry.At:yyyyMMdd-HHmmss}-{entry.Id}.json";
        var path = Path.Combine(_directory, name);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, Json), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunLogEntry>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("*.json")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Take(limit);

        var entries = new List<RunLogEntry>();

        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                var entry = JsonSerializer.Deserialize<RunLogEntry>(text, Json);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A corrupt file should not take the whole log down with it.
            }
        }

        return entries;
    }

    public async Task<RunLogEntry?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var entries = await ListAsync(200, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => e.Id == id);
    }
}
