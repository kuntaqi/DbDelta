namespace DbDelta.Api.Contracts;

public sealed record ObjectDetail(
    ObjectSummary Summary,
    IReadOnlyList<PropertyDto> Properties,
    IReadOnlyList<ObjectSummary> Children,
    IReadOnlyList<PropertyDto> ChildProperties,
    string? SourceDefinition,
    string? TargetDefinition,
    IReadOnlyList<string> PlannedStatements);
