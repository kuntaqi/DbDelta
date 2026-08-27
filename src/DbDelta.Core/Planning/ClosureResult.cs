using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

public sealed class ClosureResult
{
    // What the script should actually emit: everything picked, plus everything picked needs.
    public required IReadOnlySet<ObjectIdentity> Selection { get; init; }

    public required IReadOnlyList<RequiredObject> Required { get; init; }

    // Prerequisites that were refused: something in the plan needs them, and they carry an exclusion.
    // Closure does not override a decision already taken, so these are named and left out — the script
    // will fail on them, and this is the warning of it.
    public IReadOnlyList<RequiredObject> Blocked { get; init; } = [];

    // Prerequisites the comparison cannot supply — a referenced object that is missing from the target
    // and absent from the source too. Nothing can be emitted for these, so they are reported rather than
    // quietly dropped: the script will fail on apply and this is the only warning of it.
    public required IReadOnlyList<string> Unsatisfiable { get; init; }
}
