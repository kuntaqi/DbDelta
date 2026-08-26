namespace DbDelta.Api.Contracts;

public sealed record TableRow(
    string QualifiedName,
    long SourceRows,
    long SourceBytes,
    long TargetRows,
    long TargetBytes,
    IReadOnlyList<string> KeyColumns,
    bool HasKey,
    bool OnBothSides);
