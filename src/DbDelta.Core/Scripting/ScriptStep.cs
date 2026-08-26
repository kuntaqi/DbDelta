namespace DbDelta.Core.Scripting;

public sealed record ScriptStep(ScriptPhase Phase, string Description, string Sql);
