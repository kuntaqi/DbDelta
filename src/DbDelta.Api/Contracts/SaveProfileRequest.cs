namespace DbDelta.Api.Contracts;

// No password field, on purpose. The rule "a profile stores no password" is worth more as something the
// shape of the request cannot express than as something the store remembers to strip.
public sealed record SaveProfileRequest(
    string Name,
    string? Server = null,
    int? Port = null,
    string? Database = null,
    string? Authentication = null,
    string? Username = null,
    bool TrustServerCertificate = true,
    string? ConnectionString = null);
