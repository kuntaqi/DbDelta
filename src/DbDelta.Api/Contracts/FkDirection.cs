namespace DbDelta.Api.Contracts;

// Parents are what must exist first; children are what breaks if referenced rows are missing. At depth 1
// both fit and the choice is noise; at depth 3 on a table half the schema points at, it is the difference
// between a diagram and a smear.
public enum FkDirection
{
    Both,
    Parents,
    Children
}
