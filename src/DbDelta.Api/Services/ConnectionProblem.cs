using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Services;

// Filegroups, collation, recovery model and initial sizing are DBA decisions, and a database created
// with the wrong collation is painful to undo. A missing database is reported, never provisioned.
public static class ConnectionProblem
{
    private const int CannotOpenDatabase = 4060;
    private const int LoginFailed = 18456;
    private const int ServerNotFound = -1;

    public static (string Title, string Detail) Describe(SqlException exception, string server, string database)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.Number switch
        {
            CannotOpenDatabase => (
                "That database does not exist",
                $"'{database}' was not found on {server}. DbDelta never creates a database — filegroups, "
                    + "collation and recovery model are decisions it should not make for you. Create it first, "
                    + "then compare against it: an existing but empty database is fine."),

            LoginFailed => (
                "Login failed",
                $"Windows authentication was refused by {server}. DbDelta only connects as the signed-in user."),

            ServerNotFound => (
                "Cannot reach that server",
                $"{server} did not answer. Check the name, and for LocalDB that the instance is started "
                    + "(SqlLocalDB start MSSQLLocalDB)."),

            _ => ("Cannot reach that database", exception.Message)
        };
    }
}
