namespace DbDelta.Core.Model;

// What kind of index this is, which decides whether it can be emitted at all and how. Read from
// sys.indexes.type_desc, where it used to be collapsed to the single boolean IsClustered — so every kind
// that was not CLUSTERED became a plain non-clustered rowstore index, and a spatial one was emitted as
// CREATE INDEX over a geometry column, which the server rejects.
//
// Clustering is orthogonal and stays on IsClustered: a columnstore index can be either.
//
// The values are SQL Server's, since it is the only provider. PostgreSQL names its access methods
// differently (gist, gin, brin) and would add its own rather than be forced through these.
public enum IndexKind
{
    Rowstore,
    Columnstore,
    Xml,
    Spatial,
    Hash
}
