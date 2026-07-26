using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.Networking.Secure;

/// <summary>
/// Exposes authenticated control operations without leaking them into the
/// legacy byte protocol.
/// </summary>
internal interface ISecureControlChannel : ISecureLegacyByteTransport
{
    SecureConnectionContext ConnectionContext { get; }

    SecureBoundGamePrincipal? BoundGamePrincipal { get; }

    bool SupportsRealtimeMovement { get; }

    bool IsRealtimeMovementActive { get; }

    bool TryTakeRealtimeMovement(
        out SecureRealtimeMovementIngress ingress);

    bool TryPublishRealtimeSnapshot(
        in SecureRealtimePositionSnapshot snapshot);

    ValueTask SendGameGrantAsync(
        SecureGameGrant grant,
        CancellationToken cancellationToken);
}
