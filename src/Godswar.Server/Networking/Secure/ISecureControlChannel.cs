namespace Godswar.Server.Networking.Secure;

/// <summary>
/// Exposes authenticated control operations without leaking them into the
/// legacy byte protocol.
/// </summary>
internal interface ISecureControlChannel : ISecureLegacyByteTransport
{
    SecureConnectionContext ConnectionContext { get; }

    SecureBoundGamePrincipal? BoundGamePrincipal { get; }

    ValueTask SendGameGrantAsync(
        SecureGameGrant grant,
        CancellationToken cancellationToken);
}
