namespace DbDelta.Api.Contracts;

public sealed record ApplyRequest(
    string Confirmation,
    bool AllowDestructive);
