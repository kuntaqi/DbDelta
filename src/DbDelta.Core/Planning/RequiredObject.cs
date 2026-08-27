using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

// Carries what pulled it in, not just that something did. A plan that grew on your behalf and cannot
// say why is the trap this whole mechanism exists to avoid.
public sealed record RequiredObject(ObjectIdentity Identity, ObjectIdentity RequiredBy, string Reason);
