namespace DbDelta.Api.Contracts;

public sealed record CompareRequest(ConnectionRequest Source, ConnectionRequest Target);
