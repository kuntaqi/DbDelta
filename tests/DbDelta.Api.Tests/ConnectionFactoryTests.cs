using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Tests;

public sealed class ConnectionFactoryTests
{
    private readonly ConnectionFactory _factory = new();

    [Fact]
    public void Details_build_a_connection_string_with_windows_authentication()
    {
        var resolved = _factory.Resolve(new ConnectionRequest(Server: "HOST", Database: "MyDb"));

        var builder = new SqlConnectionStringBuilder(resolved.ConnectionString);
        Assert.True(builder.IntegratedSecurity);
        Assert.Equal("HOST", builder.DataSource);
        Assert.Equal("MyDb", builder.InitialCatalog);
    }

    [Fact]
    public void A_port_is_appended_to_the_server()
    {
        var resolved = _factory.Resolve(new ConnectionRequest(Server: "HOST", Port: 1433, Database: "MyDb"));

        Assert.Equal("HOST,1433", new SqlConnectionStringBuilder(resolved.ConnectionString).DataSource);
    }

    // A named instance already carries its own path, so appending a port would contradict it.
    [Fact]
    public void A_port_is_ignored_for_a_named_instance()
    {
        var resolved = _factory.Resolve(
            new ConnectionRequest(Server: @"(localdb)\MSSQLLocalDB", Port: 1433, Database: "MyDb"));

        Assert.Equal(@"(localdb)\MSSQLLocalDB", new SqlConnectionStringBuilder(resolved.ConnectionString).DataSource);
    }

    [Fact]
    public void A_sql_login_turns_integrated_security_off()
    {
        var resolved = _factory.Resolve(new ConnectionRequest(
            Server: "HOST",
            Database: "MyDb",
            Authentication: "SqlLogin",
            Username: "reader",
            Password: "secret"));

        var builder = new SqlConnectionStringBuilder(resolved.ConnectionString);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("reader", builder.UserID);
    }

    [Fact]
    public void A_sql_login_without_a_name_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _factory.Resolve(
            new ConnectionRequest(Server: "HOST", Database: "MyDb", Authentication: "SqlLogin")));

        Assert.Contains("login name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pasted_connection_string_is_used_as_given()
    {
        var resolved = _factory.Resolve(new ConnectionRequest(
            ConnectionString: "Server=HOST;Database=MyDb;Integrated Security=true"));

        Assert.Equal("HOST", resolved.Server);
        Assert.Equal("MyDb", resolved.Database);
        Assert.True(new SqlConnectionStringBuilder(resolved.ConnectionString).IntegratedSecurity);
    }

    // The read-only guard classifies on the server name, so the name has to come back out of a pasted
    // string in the same shape the patterns expect. Otherwise pasting a string is a way past the guard.
    [Theory]
    [InlineData("Server=DBSERVER-PROD;Database=D;Integrated Security=true", "DBSERVER-PROD")]
    [InlineData("Server=DBSERVER-PROD,1433;Database=D;Integrated Security=true", "DBSERVER-PROD")]
    [InlineData("Server=tcp:DBSERVER-PROD,1433;Database=D;Integrated Security=true", "DBSERVER-PROD")]
    [InlineData("Data Source=tcp:DBSERVER-PROD;Initial Catalog=D;Integrated Security=true", "DBSERVER-PROD")]
    public void The_server_name_is_recovered_from_a_pasted_string(string connectionString, string expected)
    {
        Assert.Equal(expected, _factory.Resolve(new ConnectionRequest(ConnectionString: connectionString)).Server);
    }

    [Fact]
    public void A_connection_string_without_a_database_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _factory.Resolve(
            new ConnectionRequest(ConnectionString: "Server=HOST;Integrated Security=true")));

        Assert.Contains("Initial Catalog", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_string_without_a_server_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => _factory.Resolve(
            new ConnectionRequest(ConnectionString: "Database=MyDb;Integrated Security=true")));
    }

    [Fact]
    public void A_malformed_connection_string_is_refused_with_the_parser_message()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _factory.Resolve(
            new ConnectionRequest(ConnectionString: "this is not a connection string")));

        Assert.Contains("could not be parsed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_request_is_refused_with_both_options_named()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _factory.Resolve(new ConnectionRequest()));

        Assert.Contains("connection string", error.Message, StringComparison.Ordinal);
        Assert.Contains("server and a database", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_string_wins_when_both_are_supplied()
    {
        var resolved = _factory.Resolve(new ConnectionRequest(
            ConnectionString: "Server=FROM-STRING;Database=StringDb;Integrated Security=true",
            Server: "FROM-DETAILS",
            Database: "DetailsDb"));

        Assert.Equal("FROM-STRING", resolved.Server);
        Assert.Equal("StringDb", resolved.Database);
    }
}
