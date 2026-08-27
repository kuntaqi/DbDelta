using DbDelta.Api.Contracts;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

// The plan in both its forms: the one the apply path runs, and the one the SPA renders. They are the same
// plan, but a staged step's rows only exist in the first — which is why apply cannot work from the DTO.
public sealed class BuiltScript
{
    public required SyncScript Script { get; init; }

    public required ScriptResponse Response { get; init; }
}
