namespace DbDelta.Api.Services;

// A saved intent to connect, and deliberately not a saved connection. Everything here is the half of a
// connection that is not a secret; the password is asked for each session and never leaves memory.
//
// Username is stored, and that is a slight widening of what the plan first listed. A username cannot be
// replayed on its own — it is not the credential, it is the other half of "which connection is this" — and
// leaving it out would mean a SQL-login profile still had two things to retype instead of one. The risk the
// no-password rule exists for is a file that lets the tool write to a server with nobody present, and a
// username does not create it.
public sealed record ConnectionProfile(
    string Name,
    string Server,
    int? Port,
    string Database,
    string Authentication,
    string? Username,
    bool TrustServerCertificate);
