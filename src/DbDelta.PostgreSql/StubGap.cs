namespace DbDelta.PostgreSql;

// Every member this stub does not implement throws through here. A uniform message keeps a caller from
// mistaking a stub for a provider that answered — a null or an empty list would read like a real result.
internal static class StubGap
{
    public static NotSupportedException For(string member) =>
        new($"The PostgreSQL provider is a stub; {member} is not implemented yet.");
}
