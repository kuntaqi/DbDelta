using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Tests;

public sealed class ServerClassifierTests
{
    private static ServerClassifier Classifier() => new(Options.Create(new SafetyOptions
    {
        ReadOnlyServers = ["DBSERVER-PROD"],
        Environments = new Dictionary<string, string[]>
        {
            ["Dev"] = ["(localdb)*", "*-DEV", "*-U02"],
            ["Uat"] = ["*-UAT", "*-U01"],
            ["Prod"] = ["*-PROD", "*-P02"]
        }
    }));

    [Theory]
    [InlineData(@"(localdb)\MSSQLLocalDB", EnvironmentClass.Dev)]
    [InlineData("DBSERVER-DEV", EnvironmentClass.Dev)]
    [InlineData("DBSERVER-UAT", EnvironmentClass.Uat)]
    [InlineData("DBSERVER-PROD", EnvironmentClass.Prod)]
    [InlineData("SOMETHING-ELSE", EnvironmentClass.Unknown)]
    public void Servers_are_classified_from_their_name(string server, EnvironmentClass expected) =>
        Assert.Equal(expected, Classifier().Classify(server));

    [Fact]
    public void Classification_is_case_insensitive() =>
        Assert.Equal(EnvironmentClass.Uat, Classifier().Classify("dbserver-uat"));

    // Anything classified as production is read-only whether or not it was listed by name, so a new
    // production server cannot be missed just because nobody added it to the list.
    [Fact]
    public void A_production_server_is_read_only_without_being_listed() =>
        Assert.True(Classifier().IsReadOnly("DBSERVER-PROD"));

    [Fact]
    public void A_listed_server_is_read_only_even_when_its_environment_is_unknown() =>
        Assert.True(Classifier().IsReadOnly("DBSERVER-PROD"));

    [Theory]
    [InlineData("DBSERVER-UAT")]
    [InlineData("DBSERVER-DEV")]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    public void Dev_and_uat_servers_stay_writable(string server) =>
        Assert.False(Classifier().IsReadOnly(server));
}
