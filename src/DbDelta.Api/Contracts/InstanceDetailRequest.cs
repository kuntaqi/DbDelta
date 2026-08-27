namespace DbDelta.Api.Contracts;

// An empty Databases list means every database on the instance. Naming them lets the screen describe one
// row without paying for the rest.
public sealed record InstanceDetailRequest(
    ConnectionRequest Connection,
    IReadOnlyList<string> Databases);
