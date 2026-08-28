namespace DbDelta.Api.Services;

// Where the two things this tool keeps between sessions live: the connection profiles and the run log.
//
// It defaults to the user's own application-data directory, which is the right answer when someone runs
// the API directly and the wrong one inside a container, where that path lands in the writable layer and
// takes the run log with it the moment the container is recreated. An operator who cannot predict the path
// cannot mount a volume at it, so the path is a setting.
//
// `DbDelta__DataDirectory` as an environment variable, `DbDelta:DataDirectory` in configuration.
public sealed class StorageOptions
{
    public const string SectionName = "DbDelta";

    public string? DataDirectory { get; init; }

    // Resolved once, so a relative path cannot mean two different directories depending on who asks.
    public string Resolve()
    {
        var directory = string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DbDelta")
            : Path.GetFullPath(DataDirectory);

        Directory.CreateDirectory(directory);
        return directory;
    }
}
