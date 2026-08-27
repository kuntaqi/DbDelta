using DbDelta.Api.Contracts;
using DbDelta.Core.Providers;
using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Services;

// Pointing at a server instead of a database. Everything else in this tool starts from a pair, which means
// there was no way to answer "what is on this instance" without already knowing what to compare.
//
// Two passes, and the split is the cost. The list is one query against server-level catalogs and answers
// instantly however many databases there are. Collation and object counts need a connection each, so they
// are a second call the user asks for — the same reasoning that makes the whole-database scan a button.
public sealed class InstanceService
{
    private readonly IDatabaseProvider _provider;
    private readonly ConnectionFactory _connections;
    private readonly ServerClassifier _classifier;

    public InstanceService(
        IDatabaseProvider provider,
        ConnectionFactory connections,
        ServerClassifier classifier)
    {
        _provider = provider;
        _connections = connections;
        _classifier = classifier;
    }

    public async Task<InstanceSurveyResponse> SurveyAsync(
        ConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = _connections.Resolve(request);
        var databases = await _provider.ListDatabasesAsync(resolved.ConnectionString, cancellationToken)
            .ConfigureAwait(false);

        var rows = databases
            .Select(d => new InstanceDatabaseDto(
                d.Name,
                d.State,
                d.RecoveryModel,
                d.DataBytes,
                d.LogBytes,
                d.IsReadOnly,
                d.Accessible,
                null,
                null,
                null,
                null,
                null))
            .ToList();

        return new InstanceSurveyResponse(
            resolved.Server,
            _classifier.Classify(resolved.Server),
            _classifier.IsReadOnly(resolved.Server),
            rows,
            // Sizes come from sys.master_files; a login without it still gets the list, and saying so beats
            // showing a column of zeroes as if the databases were empty.
            rows.Count > 0 && rows.All(r => r.DataBytes == 0)
                ? "File sizes were not readable on this server, so every size reads zero. The list itself is complete."
                : null);
    }

    // One connection per database, sequentially. Parallelising would open as many connections as the
    // instance has databases, which is a poor way to treat a server someone else is also using.
    public async Task<InstanceSurveyResponse> DescribeAsync(
        InstanceDetailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var survey = await SurveyAsync(request.Connection, cancellationToken).ConfigureAwait(false);
        var wanted = request.Databases.Count == 0
            ? survey.Databases.Select(d => d.Name).ToList()
            : request.Databases.ToList();

        var resolved = _connections.Resolve(request.Connection);
        var described = new List<InstanceDatabaseDto>();

        foreach (var row in survey.Databases)
        {
            if (!wanted.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
            {
                described.Add(row);
                continue;
            }

            // Not accessible is knowable without connecting, so it is not worth a failed connection to
            // rediscover. HAS_DBACCESS already said so.
            if (!row.Accessible || !string.Equals(row.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                described.Add(row with
                {
                    Problem = $"{row.State.ToLowerInvariant()}{(row.Accessible ? string.Empty : ", and not accessible to this login")}"
                });

                continue;
            }

            var detail = await _provider
                .DescribeDatabaseAsync(Retarget(resolved.ConnectionString, row.Name), cancellationToken)
                .ConfigureAwait(false);

            described.Add(row with
            {
                Collation = detail.Collation,
                Tables = detail.Tables,
                Views = detail.Views,
                Routines = detail.Routines,
                Problem = detail.Problem
            });
        }

        return survey with { Databases = described };
    }

    // The same credentials pointed at a different database. Rebuilding from the resolved string rather than
    // from the request keeps a pasted connection string working here too.
    private static string Retarget(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
}
