namespace DbDelta.Api.Contracts;

// The server name is pulled back out of whatever was supplied, because the read-only guard classifies
// on it. Pasting a connection string must not become a way around that.
public sealed record ResolvedConnection(string ConnectionString, string Server, string Database);
