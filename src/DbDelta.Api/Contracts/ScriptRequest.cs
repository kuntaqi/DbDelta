namespace DbDelta.Api.Contracts;

// No include list: what goes in the plan is what was picked, held on the session. Passing it per
// request would let a caller quietly widen the plan past what the screen showed.
public sealed record ScriptRequest();
