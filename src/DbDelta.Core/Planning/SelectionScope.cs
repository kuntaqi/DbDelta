namespace DbDelta.Core.Planning;

// Ordered widest-first: a wider scope subsumes every narrower selection beneath it.
public enum SelectionScope
{
    Database,
    Table,
    Row
}
