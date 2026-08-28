using System.Net;

namespace DbDelta.Api.Services;

// DbDelta has no authentication. That is defensible for what it is — a local, single-user tool — and it
// stops being defensible the moment the port is reachable from another machine, because anyone who can
// open the page can compare, script and, against a server not on the read-only list, apply changes to any
// database the process can connect to.
//
// So binding beyond loopback is refused rather than warned about, on the same reasoning as the read-only
// server guard: the cost of being wrong is not symmetric, and a warning at startup is read once.
//
// The awkward case is a container, where binding to 0.0.0.0 is not a choice — a process listening on
// 127.0.0.1 inside a container is unreachable even through a published port, so the base images set
// ASPNETCORE_URLS to all interfaces and every image has to. Inside a container the binding therefore says
// nothing about who can reach it; what decides that is how the port was published, which the process cannot
// see. Refusing there would block the supported Docker path for a fact it cannot check, so the guard defers
// and says what it could not verify. The compose file publishes to 127.0.0.1 and explains why.
public static class NetworkExposureGuard
{
    public const string AllowSetting = "Safety:AllowRemoteAccess";

    // Kestrel takes both of these to mean "every interface", alongside 0.0.0.0 and [::].
    private static readonly string[] Wildcards = ["+", "*", "0.0.0.0", "[::]", "::"];

    public static ExposureVerdict Evaluate(
        IEnumerable<string> urls,
        bool allowRemoteAccess,
        bool inContainer)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var exposed = urls
            .SelectMany(u => u.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(u => !IsLoopback(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (exposed.Count == 0)
        {
            return new ExposureVerdict(ExposureDecision.Loopback, null, exposed);
        }

        if (allowRemoteAccess)
        {
            return new ExposureVerdict(
                ExposureDecision.AllowedExplicitly,
                $"DbDelta is listening on {string.Join(", ", exposed)} and {AllowSetting} is set, so anyone "
                + "who can reach it can change any database it can connect to. There is no login.",
                exposed);
        }

        if (inContainer)
        {
            return new ExposureVerdict(
                ExposureDecision.AllowedInContainer,
                $"DbDelta is listening on {string.Join(", ", exposed)}, which is how it has to be inside a "
                + "container. Whether anyone else can reach it depends on how the port was published, which "
                + "this process cannot see - publish it to 127.0.0.1 unless you mean to share it. There is "
                + "no login.",
                exposed);
        }

        return new ExposureVerdict(
            ExposureDecision.Refused,
            $"Refusing to start. DbDelta would listen on {string.Join(", ", exposed)}, which is reachable "
            + "from other machines, and it has no authentication of any kind: anyone who can open the page "
            + "can apply changes to any database this process can connect to.\n\n"
            + "Listen on loopback instead (http://localhost:5199), or, if exposing it is deliberate and "
            + $"the network is trusted, set {AllowSetting}=true.",
            exposed);
    }

    // Host-only parsing, because that is all this needs and a URL with a wildcard host is not a valid Uri.
    private static bool IsLoopback(string url)
    {
        var host = HostOf(url);

        if (host.Length == 0 || Wildcards.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 127.0.0.0/8 in full, not just 127.0.0.1, and ::1 — IPAddress.IsLoopback knows both.
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private static string HostOf(string url)
    {
        var text = url.Trim();

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        // Trailing path first, so a path containing a colon cannot be read as a port.
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            text = text[..slash];
        }

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? text[..(close + 1)] : text;
        }

        var colon = text.LastIndexOf(':');
        return colon >= 0 ? text[..colon] : text;
    }
}
