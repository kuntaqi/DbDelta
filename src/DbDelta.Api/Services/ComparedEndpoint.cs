namespace DbDelta.Api.Services;

// One side of a pair that was compared, and the same half of a connection a profile keeps: everything
// except the secret. A profile has a name because someone chose to save it; this is remembered on the
// user's behalf, so there is nothing to call it and no field pretending there is.
//
// The no-password rule is held here the way it is held for profiles — by the shape. There is no field for
// one, so nothing downstream has to remember to strip it.
public sealed record ComparedEndpoint(
    string Server,
    int? Port,
    string Database,
    string Authentication,
    string? Username,
    bool TrustServerCertificate);
