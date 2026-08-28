namespace DbDelta.Api.Services;

// Message is null only when there is nothing worth saying, which is the loopback case. The exposed
// addresses are carried separately so a caller can log them without parsing the sentence.
public sealed record ExposureVerdict(
    ExposureDecision Decision,
    string? Message,
    IReadOnlyList<string> ExposedUrls)
{
    public bool ShouldRefuse => Decision == ExposureDecision.Refused;
}
