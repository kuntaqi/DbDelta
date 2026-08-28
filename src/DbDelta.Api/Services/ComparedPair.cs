namespace DbDelta.Api.Services;

// A pair that was compared, and compared successfully — a pair that could not connect is not worth
// offering back. The timestamp is what orders the list; it is not shown as a fact about the databases.
public sealed record ComparedPair(ComparedEndpoint Source, ComparedEndpoint Target, DateTimeOffset At)
{
    // What makes two entries the same pair. Server and database on both sides, case-insensitively, because
    // that is what identifies a comparison — not the auth mode it happened to be made with.
    public string Key =>
        string.Join(
            "\u0001",
            Source.Server.ToUpperInvariant(),
            Source.Database.ToUpperInvariant(),
            Target.Server.ToUpperInvariant(),
            Target.Database.ToUpperInvariant());
}
