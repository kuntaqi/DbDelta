namespace DbDelta.Core.Scripting;

// What a table's row picks came to, kept beside the rows so the plan can say "3 of 340" instead of "3".
// Parent closure may add rows after this is set, and it deliberately does not update: these numbers
// describe what a person chose, not what the plan ended up carrying.
public sealed record RowNarrowing(int Picked, int Available, IReadOnlyList<string> Unmatched);
