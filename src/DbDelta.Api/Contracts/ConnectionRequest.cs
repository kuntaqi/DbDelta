namespace DbDelta.Api.Contracts;

// Windows authentication only for now, so no credential ever crosses from the browser.
public sealed record ConnectionRequest(string Server, string Database);
