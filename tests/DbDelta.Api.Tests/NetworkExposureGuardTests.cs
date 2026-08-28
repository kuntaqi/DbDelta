using DbDelta.Api.Services;

namespace DbDelta.Api.Tests;

// The tool has no authentication, so the address it listens on is the whole of its access control. These
// are the cases that decide whether it starts.
public sealed class NetworkExposureGuardTests
{
    private static ExposureVerdict Evaluate(
        string urls,
        bool allow = false,
        bool container = false) =>
        NetworkExposureGuard.Evaluate([urls], allow, container);

    [Theory]
    [InlineData("http://localhost:5199")]
    [InlineData("http://LOCALHOST:5199")]
    [InlineData("http://127.0.0.1:5199")]
    [InlineData("http://[::1]:5199")]
    [InlineData("https://localhost:7272")]
    // 127.0.0.0/8 is all loopback, not just .1 — IPAddress.IsLoopback knows that and a string compare
    // against "127.0.0.1" would not.
    [InlineData("http://127.0.0.2:5199")]
    [InlineData("http://127.9.9.9:5199")]
    public void Loopback_starts_and_says_nothing(string url)
    {
        var verdict = Evaluate(url);

        Assert.Equal(ExposureDecision.Loopback, verdict.Decision);
        Assert.False(verdict.ShouldRefuse);
        Assert.Null(verdict.Message);
        Assert.Empty(verdict.ExposedUrls);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5199")]
    [InlineData("http://+:5199")]
    [InlineData("http://*:5199")]
    [InlineData("http://[::]:5199")]
    [InlineData("http://192.168.1.20:5199")]
    [InlineData("http://dbdelta.internal:5199")]
    public void Anything_reachable_from_another_machine_is_refused(string url)
    {
        var verdict = Evaluate(url);

        Assert.Equal(ExposureDecision.Refused, verdict.Decision);
        Assert.True(verdict.ShouldRefuse);
        Assert.Contains("no authentication", verdict.Message!, StringComparison.Ordinal);
        Assert.Contains(NetworkExposureGuard.AllowSetting, verdict.Message!, StringComparison.Ordinal);
    }

    // A semicolon-separated list is how ASPNETCORE_URLS carries more than one, and one bad entry is enough.
    [Fact]
    public void One_exposed_address_among_loopback_ones_still_refuses()
    {
        var verdict = Evaluate("http://localhost:5199;http://0.0.0.0:8080");

        Assert.True(verdict.ShouldRefuse);
        Assert.Equal(["http://0.0.0.0:8080"], verdict.ExposedUrls);
    }

    [Fact]
    public void The_refusal_names_the_addresses_so_it_can_be_acted_on()
    {
        var verdict = Evaluate("http://192.168.1.20:5199");

        Assert.Contains("192.168.1.20", verdict.Message!, StringComparison.Ordinal);
        Assert.Equal(["http://192.168.1.20:5199"], verdict.ExposedUrls);
    }

    // Deliberate exposure is allowed, and still says what it means — the setting turns a refusal into a
    // warning, not into silence.
    [Fact]
    public void The_setting_permits_exposure_but_does_not_silence_it()
    {
        var verdict = Evaluate("http://0.0.0.0:5199", allow: true);

        Assert.Equal(ExposureDecision.AllowedExplicitly, verdict.Decision);
        Assert.False(verdict.ShouldRefuse);
        Assert.Contains("no login", verdict.Message!, StringComparison.Ordinal);
    }

    // A container has no choice: a process on 127.0.0.1 inside one is unreachable even through a published
    // port. Refusing there would block the supported Docker path over a fact the process cannot check.
    [Fact]
    public void A_container_is_allowed_to_bind_all_interfaces_and_told_what_was_not_checked()
    {
        var verdict = Evaluate("http://+:8080", container: true);

        Assert.Equal(ExposureDecision.AllowedInContainer, verdict.Decision);
        Assert.False(verdict.ShouldRefuse);
        Assert.Contains("how the port was published", verdict.Message!, StringComparison.Ordinal);
    }

    // The container allowance is not a general one: loopback in a container is still just loopback.
    [Fact]
    public void A_container_on_loopback_is_still_the_quiet_case()
    {
        Assert.Equal(ExposureDecision.Loopback, Evaluate("http://localhost:8080", container: true).Decision);
    }

    // Nothing configured means Kestrel's own default, which is loopback. Refusing here would refuse every
    // plain `dotnet run`.
    [Fact]
    public void No_configured_url_is_not_treated_as_exposure()
    {
        var verdict = NetworkExposureGuard.Evaluate([], allowRemoteAccess: false, inContainer: false);

        Assert.Equal(ExposureDecision.Loopback, verdict.Decision);
    }

    [Theory]
    [InlineData("http://localhost:5199/")]
    [InlineData("http://localhost/")]
    [InlineData("localhost:5199")]
    public void A_trailing_path_or_a_missing_scheme_does_not_confuse_the_host(string url)
    {
        Assert.Equal(ExposureDecision.Loopback, Evaluate(url).Decision);
    }

    // The bracketed form has colons inside it, so taking everything before the last colon would leave a
    // fragment that parses as nothing and read as exposed.
    [Fact]
    public void An_ipv6_loopback_without_a_port_is_recognised()
    {
        Assert.Equal(ExposureDecision.Loopback, Evaluate("http://[::1]").Decision);
    }

    [Fact]
    public void An_ipv6_address_that_is_not_loopback_is_refused()
    {
        Assert.True(Evaluate("http://[2001:db8::1]:5199").ShouldRefuse);
    }

    // These messages are the first thing a user sees, and they are written to a console whose code page is
    // not UTF-8 by default on Windows. An em-dash in one arrives as mojibake, which is a poor introduction
    // to a tool asking to be trusted with a database.
    [Theory]
    [InlineData("http://0.0.0.0:5199", false, false)]
    [InlineData("http://0.0.0.0:5199", true, false)]
    [InlineData("http://0.0.0.0:5199", false, true)]
    public void Every_message_is_plain_ascii(string url, bool allow, bool container)
    {
        var message = Evaluate(url, allow, container).Message;

        Assert.NotNull(message);
        Assert.DoesNotContain(message, c => c > (char)127);
    }

    // Two entries pointing at the same exposed address are one problem, not two.
    [Fact]
    public void Duplicate_exposed_addresses_are_reported_once()
    {
        var verdict = NetworkExposureGuard.Evaluate(
            ["http://0.0.0.0:5199", "http://0.0.0.0:5199"], allowRemoteAccess: false, inContainer: false);

        Assert.Single(verdict.ExposedUrls);
    }
}
