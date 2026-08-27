using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

// How a type is written into T-SQL. DataTypeSpec renders itself engine-neutrally — NVARCHAR(40) for a
// built-in, dbo.PhoneNumber for a user-defined one — and the second of those needs quoting before it can
// go into a statement. A name is not a keyword: [dbo].[PhoneNumber] is the only form that resolves when
// the schema or the type is called something T-SQL would otherwise read as its own.
internal static class SqlTypeText
{
    public static string Declare(DataTypeSpec type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!type.IsUserDefined)
        {
            return type.ToString();
        }

        var quoter = SqlServerQuoter.Instance;

        return type.Schema is null
            ? quoter.Quote(type.Name)
            : $"{quoter.Quote(type.Schema)}.{quoter.Quote(type.Name)}";
    }
}
