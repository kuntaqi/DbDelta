namespace DbDelta.Core.Scripting;

public interface IDataScriptEmitter
{
    // maxInlineBytes is where literal SQL stops being the right shape for a table's rows. Past it the
    // rows travel beside the script through a staging table instead of being written into it.
    IReadOnlyList<ScriptStep> Emit(TableDataChanges changes, long maxInlineBytes);
}
