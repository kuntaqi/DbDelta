using System.Text.Json;
using DbDelta.Api.Contracts;
using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Services;

// One file next to the run log, in the user's own profile directory, holding the non-secret half of the
// connections worth keeping. A profile shortens the retyping; it does not restore a session, and it cannot
// open a connection on its own under a SQL login.
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public ProfileStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DbDelta");

        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "profiles.json");
    }

    public async Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(text, Json) ?? [];

            return profiles
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses should not stop the tool from being used. It is a
            // convenience file: reporting it empty loses nothing that cannot be retyped.
            return [];
        }
    }

    public async Task<IReadOnlyList<ConnectionProfile>> SaveAsync(
        SaveProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = Build(request);
        var profiles = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();

        // Saving over a name replaces it. Two profiles with one name would leave which of them loads to
        // whichever the file lists first.
        profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        profiles.Add(profile);

        await WriteAsync(profiles, cancellationToken).ConfigureAwait(false);

        return await ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionProfile>> DeleteAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var profiles = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();

        profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        await WriteAsync(profiles, cancellationToken).ConfigureAwait(false);

        return await ListAsync(cancellationToken).ConfigureAwait(false);
    }

    // A pasted connection string can carry a password, so it is taken apart rather than stored. What comes
    // out is the same shape a typed connection produces, minus the secret — refusing to save one at all
    // would be safe and needlessly unhelpful.
    public static ConnectionProfile Build(SaveProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked here rather than at the endpoint: a nameless profile is not a valid profile, and the rule
        // belongs with the thing it is a rule about.
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("A profile needs a name to be found by later.");
        }

        if (!string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            var builder = new SqlConnectionStringBuilder(request.ConnectionString);
            var (server, port) = Split(builder.DataSource);

            return new ConnectionProfile(
                request.Name.Trim(),
                server,
                port,
                builder.InitialCatalog,
                builder.IntegratedSecurity ? "Windows" : "SqlLogin",
                builder.IntegratedSecurity ? null : NullIfEmpty(builder.UserID),
                builder.TrustServerCertificate);
        }

        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Database))
        {
            throw new InvalidOperationException(
                "A profile needs at least a server and a database — those are what it exists to remember.");
        }

        var sqlLogin = string.Equals(request.Authentication, "SqlLogin", StringComparison.OrdinalIgnoreCase);

        return new ConnectionProfile(
            request.Name.Trim(),
            request.Server.Trim(),
            request.Port,
            request.Database.Trim(),
            sqlLogin ? "SqlLogin" : "Windows",
            sqlLogin ? NullIfEmpty(request.Username) : null,
            request.TrustServerCertificate);
    }

    // "HOST,1433" carries its port in the data source; a named instance carries a path instead and must
    // keep it whole.
    private static (string Server, int? Port) Split(string dataSource)
    {
        var comma = dataSource.LastIndexOf(',');

        return comma > 0 && int.TryParse(dataSource[(comma + 1)..].Trim(), out var port)
            ? (dataSource[..comma].Trim(), port)
            : (dataSource.Trim(), null);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task WriteAsync(List<ConnectionProfile> profiles, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(profiles, Json), cancellationToken)
            .ConfigureAwait(false);
}
