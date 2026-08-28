using DbDelta.Api.Services;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Tests;

// The two things this tool keeps between sessions — profiles and the run log — used to be pinned to the
// user's application-data directory. That is correct for someone running the API directly and wrong inside
// a container, where the path lands in the writable layer and the run log disappears with the container.
// An operator who cannot predict the path cannot mount a volume at it.
//
// These run against a real temporary directory, because the whole point is where the file ends up.
public sealed class StorageOptionsTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "DbDeltaStorageTests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void With_nothing_configured_the_directory_is_the_users_own_application_data()
    {
        var resolved = new StorageOptions().Resolve();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbDelta"),
            resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_blank_setting_falls_back_rather_than_resolving_to_nothing(string? configured)
    {
        var resolved = new StorageOptions { DataDirectory = configured }.Resolve();

        Assert.Contains("DbDelta", resolved, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void A_configured_directory_is_used_and_created()
    {
        var target = Path.Combine(_scratch, "state");

        Assert.False(Directory.Exists(target));

        var resolved = new StorageOptions { DataDirectory = target }.Resolve();

        Assert.Equal(target, resolved);
        Assert.True(Directory.Exists(target), "the directory has to exist before anything writes into it");
    }

    // Relative is resolved once, so the same setting cannot mean two directories depending on the working
    // directory of whoever asked.
    [Fact]
    public void A_relative_directory_is_made_absolute()
    {
        var resolved = new StorageOptions { DataDirectory = "dbdelta-state" }.Resolve();

        try
        {
            Assert.True(Path.IsPathFullyQualified(resolved));
            Assert.EndsWith("dbdelta-state", resolved, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    // The reason the setting exists: point it somewhere and both stores land there together, so one mounted
    // volume covers everything that has to outlive the process.
    [Fact]
    public async Task Both_stores_write_under_the_configured_directory()
    {
        var options = Options.Create(new StorageOptions { DataDirectory = _scratch });

        var profiles = new ProfileStore(options);
        await profiles.SaveAsync(new Contracts.SaveProfileRequest(
            "Dev", Server: "DBSERVER-DEV", Database: "AppDev", Authentication: "Windows"));

        var runs = new RunLogStore(options);
        await runs.WriteAsync(new RunLogEntry
        {
            Id = "abc123",
            At = DateTimeOffset.Now,
            Action = "Apply",
            SourceDatabase = "AppDev",
            TargetServer = "DBSERVER-DEV",
            TargetDatabase = "AppDev",
            Outcome = "Committed",
            StepCount = 1,
            DurationMs = 1,
            Sql = "-- nothing"
        });

        Assert.True(File.Exists(Path.Combine(_scratch, "profiles.json")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_scratch, "runs"), "*.json"));

        // And nothing was written to the default location by either of them.
        Assert.Single(Directory.GetFiles(_scratch, "*.json"));
    }
}
