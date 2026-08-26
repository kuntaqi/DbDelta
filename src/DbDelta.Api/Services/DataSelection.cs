using DbDelta.Core.Data;

namespace DbDelta.Api.Services;

public sealed record DataSelection(TableDataMode Mode, int TopCount, string? Filter);
