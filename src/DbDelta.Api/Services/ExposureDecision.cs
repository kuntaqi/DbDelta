namespace DbDelta.Api.Services;

public enum ExposureDecision
{
    // Nothing beyond loopback: the normal case, and the only one that needs saying nothing.
    Loopback,

    // Beyond loopback, and someone said so deliberately.
    AllowedExplicitly,

    // Beyond loopback because a container leaves no other option. Reachability is decided by the port
    // publish, which this process cannot see.
    AllowedInContainer,

    Refused
}
