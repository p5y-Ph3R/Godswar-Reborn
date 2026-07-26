using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpSessionLease : IDisposable
{
    private SecureUdpSessionAuthority? _authority;
    private readonly SecureUdpConnectionKey _connectionId;
    private readonly long _generation;

    internal SecureUdpSessionLease(
        SecureUdpSessionAuthority authority,
        SecureUdpConnectionKey connectionId,
        long generation)
    {
        _authority = authority ??
            throw new ArgumentNullException(nameof(authority));
        _connectionId = connectionId;
        _generation = generation;
    }

    public bool TryCopyGrantMaterial(
        Span<byte> connectionIdDestination,
        Span<byte> proofKeyDestination,
        out ulong expiryUnixMilliseconds)
    {
        expiryUnixMilliseconds = 0;
        if (connectionIdDestination.Length <
                SecureUdpBindingConstants.ConnectionIdBytes ||
            proofKeyDestination.Length <
                SecureUdpTlsProofAuthenticator.KeyBytes ||
            connectionIdDestination.Overlaps(proofKeyDestination))
        {
            return false;
        }

        var connectionIdOutput = connectionIdDestination[
            ..SecureUdpBindingConstants.ConnectionIdBytes];
        var proofKeyOutput = proofKeyDestination[
            ..SecureUdpTlsProofAuthenticator.KeyBytes];
        connectionIdOutput.Clear();
        proofKeyOutput.Clear();
        var authority = Volatile.Read(ref _authority);
        return authority is not null &&
            authority.TryCopyGrantMaterial(
                _connectionId,
                _generation,
                connectionIdOutput,
                proofKeyOutput,
                out expiryUnixMilliseconds);
    }

    public SecureUdpBindingCapabilities Capabilities
    {
        get
        {
            var authority = Volatile.Read(ref _authority);
            return authority is not null &&
                authority.SupportsRealtimeMovement(
                    _connectionId,
                    _generation)
                ? authority.BindingCapabilities
                : SecureUdpBindingCapabilities.None;
        }
    }

    public bool SupportsRealtimeMovement =>
        Capabilities.HasFlag(
            SecureUdpBindingCapabilities.AuthoritativeMovement);

    public bool IsRealtimeMovementActive =>
        Volatile.Read(ref _authority)?
            .IsRealtimeMovementActive(
                _connectionId,
                _generation) == true;

    public SecureRealtimeMovementOfferResult OfferTlsMovement(
        ReadOnlySpan<byte> payload)
    {
        var authority = Volatile.Read(ref _authority);
        return authority is null
            ? new SecureRealtimeMovementOfferResult(
                SecureRealtimeMovementOfferStatus.SessionUnavailable,
                0,
                default,
                0)
            : authority.OfferTlsMovement(
                _connectionId,
                _generation,
                payload);
    }

    public bool TryTakeRealtimeMovement(
        out SecureRealtimeMovementIngress ingress)
    {
        var authority = Volatile.Read(ref _authority);
        if (authority is not null &&
            authority.TryTakeRealtimeMovement(
                _connectionId,
                _generation,
                out ingress))
        {
            return true;
        }

        ingress = default;
        return false;
    }

    public bool TryPublishRealtimeSnapshot(
        in SecureRealtimePositionSnapshot snapshot)
    {
        var authority = Volatile.Read(ref _authority);
        return authority is not null &&
            authority.TryPublishRealtimeSnapshot(
                _connectionId,
                _generation,
                snapshot);
    }

    public void Dispose()
    {
        var authority = Interlocked.Exchange(ref _authority, null);
        authority?.Release(_connectionId, _generation);
    }
}
