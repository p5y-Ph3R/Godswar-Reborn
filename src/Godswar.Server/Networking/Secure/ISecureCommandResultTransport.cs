namespace Godswar.Server.Networking.Secure;

// This result is carried outside the stock legacy byte stream. Implementations
// must serialize it with all other secure server-to-client control writes.
internal interface ISecureCommandResultTransport
{
    ValueTask SendLegacyCommandResultAsync(
        SecureLegacyCommandResult result,
        CancellationToken cancellationToken);
}
