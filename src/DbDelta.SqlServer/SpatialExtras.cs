namespace DbDelta.SqlServer;

// Extras keys for what a spatial index needs beyond sys.indexes. These live in the provider rather than
// on IndexDefinition because BOUNDING_BOX and a tessellation scheme are T-SQL, and ProviderExtras is
// where engine-specific facts already go — the same choice FillFactor made.
internal static class SpatialExtras
{
    public const string Scheme = "Spatial.Scheme";
    public const string BoundingBox = "Spatial.BoundingBox";
    public const string Grids = "Spatial.Grids";
    public const string CellsPerObject = "Spatial.CellsPerObject";
}
