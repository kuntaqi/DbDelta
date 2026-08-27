namespace DbDelta.Core.Scripting;

// Emission order. Tables are created bare and their foreign keys added in a later phase, which is
// what removes FK cycles as a concern instead of needing NOCHECK bracketing.
public enum ScriptPhase
{
    Schemas,
    DropForeignKeys,
    DropProgrammables,
    DropTables,
    // A type cannot be dropped while a column still uses it, and cannot be used before it exists, so it
    // brackets the tables on both sides.
    DropTypes,
    CreateTypes,
    CreateTables,
    AlterColumns,
    Keys,
    Indexes,
    CheckConstraints,
    AddForeignKeys,
    // Child rows go before parents on the way out and after them on the way in, so deletes and
    // upserts cannot share one phase.
    DataDeletes,
    DataUpserts,
    Programmables
}
