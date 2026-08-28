namespace DbDelta.Api.Contracts;

// Names one source database as corresponding to one target database, for the case where they do not share
// a name — which is every case where the environment is part of the name.
public sealed record DatabasePairingDto(string Source, string Target);
