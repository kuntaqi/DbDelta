namespace DbDelta.Api.Contracts;

// Ranges rather than numbers, and Notes is not decoration: it carries what could not be established, so
// the figures cannot be shown without the reason they are approximate being available too.
public sealed record PlanEstimateResponse(
    long SchemaBytes,
    long MinBytes,
    long MaxBytes,
    long MinRows,
    long MaxRows,
    int Tables,
    int TablesNotScanned,
    bool RowsAreExact,
    string Verdict,
    long ReviewableLimitBytes,
    IReadOnlyList<string> Notes);
