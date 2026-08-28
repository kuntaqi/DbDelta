namespace DbDelta.Core.Instances;

public enum PairKind
{
    // Same name on both servers.
    ByName,

    // Named explicitly, because the two databases do not share a name.
    Declared,

    // On the source and not on the target. Reportable, not actionable: this tool does not create
    // databases, by a decision recorded in the plan.
    SourceOnly,

    TargetOnly
}
