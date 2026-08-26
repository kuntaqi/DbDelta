namespace DbDelta.Api.Contracts;

// Two ways in: paste a connection string, or fill in the parts. Everything is optional on the wire so
// one shape serves both; ConnectionFactory decides which was meant and says so when neither is usable.
public sealed record ConnectionRequest(
    string? ConnectionString = null,
    string? Server = null,
    int? Port = null,
    string? Database = null,
    string? Authentication = null,
    string? Username = null,
    string? Password = null,
    bool TrustServerCertificate = true);
