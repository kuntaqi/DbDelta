namespace DbDelta.Core.Apply;

public enum ApplyOutcome
{
    Committed,
    RolledBack,
    Blocked,
    Drifted
}
