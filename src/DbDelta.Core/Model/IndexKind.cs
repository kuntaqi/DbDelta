namespace DbDelta.Core.Model;

// What kind of index this is, which decides whether it can be emitted at all and how. Read from
// sys.indexes.type_desc, where it used to be collapsed to the single boolean IsClustered — so every kind
// that was not CLUSTERED became a plain non-clustered rowstore index, and a spatial one was emitted as
// CREATE INDEX over a geometry column, which the server rejects.
//
// Clustering is orthogonal and stays on IsClustered: a columnstore index can be either.
//
// The first values are SQL Server's; PostgreSQL names its access methods differently and adds its own rather
// than being forced through the SQL ones. A PG btree reads as Rowstore (the ordinary index both engines share)
// and hash reuses Hash; the remaining PG methods get their own values so two indexes that differ only by
// method diff instead of both collapsing to Rowstore. These never reach the T-SQL emitter — a PG kind is only
// compared, never scripted by the SQL provider.
public enum IndexKind
{
    Rowstore,
    Columnstore,
    Xml,
    Spatial,
    Hash,
    Gin,
    Gist,
    Brin,
    Spgist
}
