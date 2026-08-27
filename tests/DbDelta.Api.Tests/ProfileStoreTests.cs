using DbDelta.Api.Contracts;
using DbDelta.Api.Services;

namespace DbDelta.Api.Tests;

// Build is where a request becomes a stored profile, and the one thing that must never survive that trip is
// the password. These test the shaping rather than the file, which is what carries the rule.
public sealed class ProfileStoreTests
{
    [Fact]
    public void A_typed_connection_keeps_everything_that_is_not_a_secret()
    {
        var profile = ProfileStore.Build(new SaveProfileRequest(
            "UAT",
            Server: "DBSERVER-UAT",
            Port: 1433,
            Database: "AppUat",
            Authentication: "SqlLogin",
            Username: "deploy",
            TrustServerCertificate: true));

        Assert.Equal("UAT", profile.Name);
        Assert.Equal("DBSERVER-UAT", profile.Server);
        Assert.Equal(1433, profile.Port);
        Assert.Equal("AppUat", profile.Database);
        Assert.Equal("SqlLogin", profile.Authentication);
        Assert.Equal("deploy", profile.Username);
        Assert.True(profile.TrustServerCertificate);
    }

    // A pasted string can carry a password, so it is taken apart rather than stored whole. Refusing to save
    // one would be safe and needlessly unhelpful; keeping it verbatim would put a secret on disk.
    [Fact]
    public void A_pasted_connection_string_is_taken_apart_and_its_password_is_not_kept()
    {
        var profile = ProfileStore.Build(new SaveProfileRequest(
            "Pasted",
            ConnectionString: "Server=DBSERVER-UAT,1433;Database=AppUat;User ID=deploy;Password=hunter2;TrustServerCertificate=true"));

        Assert.Equal("DBSERVER-UAT", profile.Server);
        Assert.Equal(1433, profile.Port);
        Assert.Equal("AppUat", profile.Database);
        Assert.Equal("SqlLogin", profile.Authentication);
        Assert.Equal("deploy", profile.Username);

        // The whole point, asserted against the serialized shape rather than field by field: nothing about
        // this profile can contain the password, whatever is added to it later.
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_connection_stores_no_username_because_there_is_none_to_store()
    {
        var profile = ProfileStore.Build(new SaveProfileRequest(
            "Dev",
            Server: "(localdb)\\MSSQLLocalDB",
            Database: "AppDev",
            Authentication: "Windows",
            Username: "left over from switching auth mode"));

        Assert.Equal("Windows", profile.Authentication);
        Assert.Null(profile.Username);
    }

    // A named instance carries its own path, so the part after the backslash is not a port.
    [Fact]
    public void A_named_instance_is_not_split_into_a_port()
    {
        var profile = ProfileStore.Build(new SaveProfileRequest(
            "Local",
            ConnectionString: "Server=(localdb)\\MSSQLLocalDB;Database=AppDev;Integrated Security=true"));

        Assert.Equal("(localdb)\\MSSQLLocalDB", profile.Server);
        Assert.Null(profile.Port);
        Assert.Equal("Windows", profile.Authentication);
    }

    [Theory]
    [InlineData("", "DBSERVER-UAT", "AppUat")]
    [InlineData("   ", "DBSERVER-UAT", "AppUat")]
    [InlineData("UAT", null, "AppUat")]
    [InlineData("UAT", "DBSERVER-UAT", null)]
    public void A_profile_without_enough_to_be_useful_is_refused(string name, string? server, string? database)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProfileStore.Build(new SaveProfileRequest(name, Server: server, Database: database)));
    }

    [Fact]
    public void Whitespace_around_the_parts_does_not_become_part_of_them()
    {
        var profile = ProfileStore.Build(new SaveProfileRequest(
            "  UAT  ",
            Server: "  DBSERVER-UAT  ",
            Database: "  AppUat  ",
            Authentication: "SqlLogin",
            Username: "  deploy  "));

        Assert.Equal("UAT", profile.Name);
        Assert.Equal("DBSERVER-UAT", profile.Server);
        Assert.Equal("AppUat", profile.Database);
        Assert.Equal("deploy", profile.Username);
    }
}
