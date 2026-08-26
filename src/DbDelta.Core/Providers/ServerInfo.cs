namespace DbDelta.Core.Providers;

public sealed record ServerInfo(
    string ServerName,
    string DatabaseName,
    string ProductVersion,
    string Edition,
    string Collation);
