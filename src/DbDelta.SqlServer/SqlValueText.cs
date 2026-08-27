using System.Globalization;

namespace DbDelta.SqlServer;

// The single place a value read from SQL Server becomes text. These values feed the grid, the generated
// script, and the value-based lookups parent closure runs, so the text has to round-trip and every
// reader has to produce the same of it: two readers formatting a datetime differently would make a row
// look absent from a parent table it is sitting in.
//
// The default ToString for a DateTime is culture-shaped and for a double drops digits, either of which
// would write different data than the source holds.
internal static class SqlValueText
{
    public static string Of(object value) => value switch
    {
        DateTime date => date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        bool flag => flag ? "1" : "0",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        Guid guid => guid.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
