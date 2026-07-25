namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpSessionRegistrationStatus : byte
{
    Registered = 1,
    CapacityExceeded = 2,
    DuplicateConnectionId = 3
}

internal enum SecureUdpSessionBindStatus : byte
{
    Bound = 1,
    AlreadyBound = 2,
    UnknownSession = 3,
    Expired = 4,
    InvalidProof = 5,
    EndpointConflict = 6,
    InvalidEndpoint = 7,
    Rebound = 8,
    ReplayRejected = 9,
    RebindRateLimited = 10
}

internal readonly record struct SecureUdpSessionRegistrationResult(
    SecureUdpSessionRegistrationStatus Status,
    SecureUdpSessionLease? Lease)
{
    public bool IsRegistered =>
        Status == SecureUdpSessionRegistrationStatus.Registered &&
        Lease is not null;
}

internal readonly record struct SecureUdpSessionAuthoritySnapshot(
    int Capacity,
    int PendingSessions,
    int BoundSessions)
{
    public int TrackedSessions => checked(PendingSessions + BoundSessions);
}
