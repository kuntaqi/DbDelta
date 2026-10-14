using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDelta.Api.Services;

// Filegroups, collation, recovery model and initial sizing are DBA decisions, and a database created
// with the wrong collation is painful to undo. A missing database is reported, never provisioned.
//
// Both engines fail through DbException, so the mapper takes that and branches: SQL Server reports integer
// error numbers, PostgreSQL five-character SQLSTATE strings, and a pure connection failure (host unreachable)
// carries neither.
public static class ConnectionProblem
{
    private const int SqlCannotOpenDatabase = 4060;
    private const int SqlLoginFailed = 18456;

    // PostgreSQL SQLSTATE classes: invalid_catalog_name, and the invalid-authorization class.
    private const string PgUndefinedDatabase = "3D000";
    private const string PgInvalidPassword = "28P01";
    private const string PgInvalidAuthorization = "28000";

    public static (string Title, string Detail) Describe(DbException exception, string server, string database)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SqlException sql => DescribeSqlServer(sql, server, database),
            PostgresException pg => DescribePostgres(pg, server, database),
            NpgsqlException => (
                "Cannot reach that server",
                $"{server} did not answer. Check the host, the port, and that PostgreSQL is accepting connections."),
            _ => ("Cannot reach that database", exception.Message),
        };
    }

    // True when the failure is specifically "the named database does not exist", which the compare endpoints
    // use to decide which of the two connections to blame.
    public static bool IsMissingDatabase(DbException exception) => exception switch
    {
        SqlException sql => sql.Number == SqlCannotOpenDatabase,
        PostgresException pg => pg.SqlState == PgUndefinedDatabase,
        _ => false,
    };

    private static (string, string) DescribeSqlServer(SqlException exception, string server, string database) =>
        exception.Number switch
        {
            SqlCannotOpenDatabase => (
                "That database does not exist",
                $"'{database}' was not found on {server}. DbDelta never creates a database — filegroups, "
                    + "collation and recovery model are decisions it should not make for you. Create it first, "
                    + "then compare against it: an existing but empty database is fine."),

            SqlLoginFailed => (
                "Login failed",
                $"Windows authentication was refused by {server}. DbDelta only connects as the signed-in user."),

            -1 => (
                "Cannot reach that server",
                $"{server} did not answer. Check the name, and for LocalDB that the instance is started "
                    + "(SqlLocalDB start MSSQLLocalDB)."),

            _ => ("Cannot reach that database", exception.Message),
        };

    private static (string, string) DescribePostgres(PostgresException exception, string server, string database) =>
        exception.SqlState switch
        {
            PgUndefinedDatabase => (
                "That database does not exist",
                $"'{database}' was not found on {server}. DbDelta never creates a database — collation and "
                    + "encoding are decisions it should not make for you. Create it first, then compare against "
                    + "it: an existing but empty database is fine."),

            PgInvalidPassword or PgInvalidAuthorization => (
                "Login failed",
                $"{server} refused the login. Check the username and password."),

            _ => ("Cannot reach that database", exception.MessageText),
        };
}
