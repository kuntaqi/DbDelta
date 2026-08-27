namespace DbDelta.Core.Data;

public sealed record FilterValidation(bool IsValid, string? Error)
{
    public static FilterValidation Ok { get; } = new(true, null);

    public static FilterValidation Rejected(string error) => new(false, error);
}
