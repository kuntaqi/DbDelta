namespace DbDelta.Core.Scripting;

// Emission order. Tables are created bare and their foreign keys added in a later phase, which is
// what removes FK cycles as a concern instead of needing NOCHECK bracketing.
public enum ScriptPhase
{
    Schemas,
    DropForeignKeys,
    DropProgrammables,
    DropTables,
    CreateTables,
    AlterColumns,
    Keys,
    Indexes,
    CheckConstraints,
    AddForeignKeys,
    Programmables
}
