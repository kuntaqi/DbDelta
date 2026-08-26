namespace DbDelta.Api.Contracts;

public sealed record ApplyRequest(
    IReadOnlyList<string> Include,
    string Confirmation,
    bool AllowDestructive);
