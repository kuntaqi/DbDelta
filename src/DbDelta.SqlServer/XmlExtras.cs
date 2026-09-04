namespace DbDelta.SqlServer;

// Extras keys for what an XML index needs beyond sys.indexes: whether it is the primary one, and for a
// secondary, which primary it hangs off and which of PATH / VALUE / PROPERTY it is.
internal static class XmlExtras
{
    public const string Kind = "Xml.Kind";
    public const string SecondaryType = "Xml.SecondaryType";
    public const string PrimaryIndex = "Xml.PrimaryIndex";

    public const string Primary = "PRIMARY_XML";
    public const string Secondary = "SECONDARY_XML";
}
