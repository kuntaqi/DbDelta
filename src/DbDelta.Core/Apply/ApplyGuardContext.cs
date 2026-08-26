namespace DbDelta.Core.Apply;

public sealed class ApplyGuardContext
{
    public required string TargetServer { get; init; }

    public required string TargetDatabase { get; init; }

    public required string Confirmation { get; init; }

    public required bool TargetIsReadOnly { get; init; }

    public bool AllowDestructive { get; init; }

    public IReadOnlyList<string> DestructiveSteps { get; init; } = [];
}
