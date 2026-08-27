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
    // A column default can read NEXT VALUE FOR a sequence, so a sequence outlives the tables that use it
    // on the way out and precedes them on the way in — the same bracketing as a type.
    DropSequences,
    CreateSequences,
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
