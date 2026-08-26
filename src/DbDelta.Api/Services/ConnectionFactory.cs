using DbDelta.Api.Contracts;
using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Services;

// The browser sends a server and a database, never a connection string. Building it here keeps
// credentials and connection options out of anything the SPA can see or tamper with.
public sealed class ConnectionFactory
{
    public string Build(ConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Server);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Database);

        return new SqlConnectionStringBuilder
        {
            DataSource = request.Server,
            InitialCatalog = request.Database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        }.ConnectionString;
    }
}
