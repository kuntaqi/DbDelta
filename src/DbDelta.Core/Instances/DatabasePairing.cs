namespace DbDelta.Core.Instances;

// One database on the source declared to correspond to one on the target.
//
// This exists because matching by name is not enough, and the convention in this project's own examples is
// why: `AppProd`, `AppUat`, `AppDev`. Where the environment is part of the database name, two servers have
// no names in common at all, and a comparison that only matched names would report every database as
// missing on both sides — which is the same shape as reporting nothing.
public sealed record DatabasePairing(string Source, string Target);
