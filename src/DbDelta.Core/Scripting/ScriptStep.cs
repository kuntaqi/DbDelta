namespace DbDelta.Core.Scripting;

// Destructive is stated by whoever emits the step, not guessed from its text. Reading the SQL for the
// word DROP was close enough while every drop was a real one — then staging arrived, and dropping a
// temporary table it had just created read as destroying user data.
public sealed record ScriptStep(
    ScriptPhase Phase,
    string Description,
    string Sql,
    BulkLoad? Load = null,
    bool Destructive = false);
