namespace DbDelta.Core.Scripting;

public interface IDataScriptEmitter
{
    IReadOnlyList<ScriptStep> Emit(TableDataChanges changes);
}
