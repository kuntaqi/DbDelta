namespace DbDelta.Core.Planning;

// A first-class "no", not the absence of a tick. Survives scope escalation so that widening to the
// whole database means "also take what I have not considered", never "forget what I decided".
public sealed record Exclusion(ChangeUnitId Unit, string Reason);
