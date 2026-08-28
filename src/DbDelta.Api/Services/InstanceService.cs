using System.Diagnostics;
using DbDelta.Api.Contracts;
using DbDelta.Core.Instances;
using DbDelta.Core.Model;
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

        var rows = databases.Select(Row).ToList();

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

    // Two servers, lined up against each other. The cheap half is two queries and answers the only thing
    // that is cheap to answer: which databases exist on both sides. Describing what is inside them costs a
    // connection per database per side, so it is opt-in — the same split as the survey, for the same reason.
    public async Task<InstanceComparisonResponse> CompareAsync(
        InstanceCompareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = _connections.Resolve(request.Source);
        var target = _connections.Resolve(request.Target);

        var stopwatch = Stopwatch.StartNew();

        var sourceDatabases = await _provider
            .ListDatabasesAsync(source.ConnectionString, cancellationToken).ConfigureAwait(false);
        var targetDatabases = await _provider
            .ListDatabasesAsync(target.ConnectionString, cancellationToken).ConfigureAwait(false);

        var match = InstanceMatcher.Match(
            sourceDatabases,
            targetDatabases,
            (request.Pairings ?? []).Select(p => new DatabasePairing(p.Source, p.Target)));

        var pairs = new List<DatabasePairDto>();

        foreach (var pair in match.Pairs)
        {
            var left = pair.Source is null ? null : Row(pair.Source);
            var right = pair.Target is null ? null : Row(pair.Target);

            if (request.Describe && pair.CanBeCompared)
            {
                left = await DescribedAsync(source.ConnectionString, left!, cancellationToken)
                    .ConfigureAwait(false);
                right = await DescribedAsync(target.ConnectionString, right!, cancellationToken)
                    .ConfigureAwait(false);
            }

            var (signal, detail) = Judge(pair, left, right, request.Describe);
            pairs.Add(new DatabasePairDto(pair.Name, pair.Kind.ToString(), pair.CanBeCompared, signal, detail, left, right));
        }

        stopwatch.Stop();

        var warnings = new List<string>(match.Problems);

        // The trap this feature exists to avoid. Where the environment is part of the database name, two
        // servers share no names at all, and a list of "only on the source" and "only on the target" reads
        // exactly like a list of differences when it is really a list of things nobody has lined up yet.
        if (match.OnBothSides == 0 && sourceDatabases.Count > 0 && targetDatabases.Count > 0)
        {
            warnings.Add(
                $"No database name appears on both servers, so nothing has been compared. {source.Server} "
                + $"and {target.Server} may still hold the same databases under different names — "
                + "AppProd against AppUat, say. Pair them up explicitly to compare them.");
        }

        if (!request.Describe && match.OnBothSides > 0)
        {
            warnings.Add(
                $"{match.OnBothSides} database(s) are on both servers and nothing has been read from them "
                + "yet. Matching names is two queries; looking inside costs a connection per database per "
                + "side, so it is a separate step.");
        }

        return new InstanceComparisonResponse(
            source.Server,
            target.Server,
            _classifier.Classify(source.Server),
            _classifier.Classify(target.Server),
            _classifier.IsReadOnly(target.Server),
            stopwatch.ElapsedMilliseconds,
            match.OnBothSides,
            match.SourceOnly,
            match.TargetOnly,
            request.Describe,
            pairs,
            warnings);
    }

    // What can be said about a pair, and — more to the point — what cannot. Equal object counts are not
    // equal schemas, and this refuses to phrase them as though they were: two databases with the same number
    // of tables can differ in every column of every one of them.
    private static (string Signal, string Detail) Judge(
        DatabasePair pair,
        InstanceDatabaseDto? source,
        InstanceDatabaseDto? target,
        bool described)
    {
        if (!pair.OnBothSides)
        {
            return pair.Kind == PairKind.SourceOnly
                ? ("SourceOnly", "Only on the source. This tool does not create databases, so a plan cannot "
                    + "fix this — create it first, then compare.")
                : ("TargetOnly", "Only on the target. Nothing in a plan will remove it.");
        }

        if (!pair.CanBeCompared)
        {
            var why = new List<string>();

            if (source is { Accessible: false })
            {
                why.Add("not accessible to this login on the source");
            }

            if (target is { Accessible: false })
            {
                why.Add("not accessible to this login on the target");
            }

            if (source is not null && !string.Equals(source.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                why.Add($"{source.State.ToLowerInvariant()} on the source");
            }

            if (target is not null && !string.Equals(target.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                why.Add($"{target.State.ToLowerInvariant()} on the target");
            }

            return ("Unreadable", $"On both servers but {string.Join(", ", why)}. Unknown, not equal.");
        }

        if (!described)
        {
            return ("NotCompared", "On both servers. Nothing has been read from it yet.");
        }

        if (source?.Problem is not null || target?.Problem is not null)
        {
            return ("Unreadable", $"On both servers but could not be read: {source?.Problem ?? target?.Problem}");
        }

        // Collation first, because it is the one difference that changes what a data compare would even
        // mean — see the collation precondition.
        if (!string.Equals(source?.Collation, target?.Collation, StringComparison.OrdinalIgnoreCase))
        {
            return ("CollationDiffers",
                $"Collations differ: {source?.Collation} against {target?.Collation}. Whether that affects a "
                + "data compare depends on the columns, which only comparing the pair can tell you.");
        }

        if (source?.Tables != target?.Tables || source?.Views != target?.Views || source?.Routines != target?.Routines)
        {
            return ("CountsDiffer",
                $"Object counts differ: {source?.Tables}/{source?.Views}/{source?.Routines} against "
                + $"{target?.Tables}/{target?.Views}/{target?.Routines} tables/views/routines.");
        }

        return ("CountsMatch",
            $"Same collation and the same {source?.Tables}/{source?.Views}/{source?.Routines} "
            + "tables/views/routines. That is not the same schema — equal counts say nothing about what is "
            + "in them. Compare the pair to find out.");
    }

    private async Task<InstanceDatabaseDto> DescribedAsync(
        string connectionString,
        InstanceDatabaseDto row,
        CancellationToken cancellationToken)
    {
        var detail = await _provider
            .DescribeDatabaseAsync(Retarget(connectionString, row.Name), cancellationToken)
            .ConfigureAwait(false);

        return row with
        {
            Collation = detail.Collation,
            Tables = detail.Tables,
            Views = detail.Views,
            Routines = detail.Routines,
            Problem = detail.Problem
        };
    }

    private static InstanceDatabaseDto Row(DatabaseSummary d) =>
        new(d.Name, d.State, d.RecoveryModel, d.DataBytes, d.LogBytes, d.IsReadOnly, d.Accessible,
            null, null, null, null, null);

    // The same credentials pointed at a different database. Rebuilding from the resolved string rather than
    // from the request keeps a pasted connection string working here too.
    private static string Retarget(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
}
