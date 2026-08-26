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
            ["Dev"] = ["(localdb)*", "*-DEV"],
            ["Uat"] = ["*-UAT"],
            ["Prod"] = ["*-PROD"]
        }
    }));

    [Theory]
    [InlineData(@"(localdb)\MSSQLLocalDB", EnvironmentClass.Dev)]
    [InlineData("HOST-SQL-DEV", EnvironmentClass.Dev)]
    [InlineData("HOST-SQL-UAT", EnvironmentClass.Uat)]
    [InlineData("HOST-SQL-PROD", EnvironmentClass.Prod)]
    [InlineData("SOMETHING-ELSE", EnvironmentClass.Unknown)]
    public void Servers_are_classified_from_their_name(string server, EnvironmentClass expected) =>
        Assert.Equal(expected, Classifier().Classify(server));

    [Fact]
    public void Classification_is_case_insensitive() =>
        Assert.Equal(EnvironmentClass.Uat, Classifier().Classify("host-sql-uat"));

    // Anything classified as production is read-only whether or not it was listed by name, so a new
    // production server cannot be missed just because nobody added it to the list.
    [Fact]
    public void A_production_server_is_read_only_without_being_listed() =>
        Assert.True(Classifier().IsReadOnly("HOST-SQL-PROD"));

    [Fact]
    public void A_listed_server_is_read_only_even_when_its_environment_is_unknown() =>
        Assert.True(Classifier().IsReadOnly("DBSERVER-PROD"));

    [Theory]
    [InlineData("HOST-SQL-UAT")]
    [InlineData("HOST-SQL-DEV")]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    public void Dev_and_uat_servers_stay_writable(string server) =>
        Assert.False(Classifier().IsReadOnly(server));
}
