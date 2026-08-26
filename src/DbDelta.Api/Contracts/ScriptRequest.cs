namespace DbDelta.Api.Contracts;

public sealed record ScriptRequest(IReadOnlyList<string> Include);
