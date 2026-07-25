using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    internal async ValueTask SendUdpBindingGrantAsync(
        SecureUdpBindingGrant grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (_endpointRole != NetworkEndpointRole.Game ||
            _boundGamePrincipal is null ||
            _udpRegistrationLease is null)
        {
            throw new InvalidOperationException(
                "Only an authority-registered secure game transport may receive a UDP binding grant.");
        }

        var payload =
            new byte[SecureProtocolConstants.UdpBindingGrantBytes];
        try
        {
            if (!SecureUdpBindingGrantCodec.TryEncode(
                    grant,
                    payload,
                    out var bytesWritten) ||
                bytesWritten != payload.Length)
            {
                throw new InvalidOperationException(
                    "The bounded UDP binding grant could not be encoded.");
            }

            await WriteControlFrameAsync(
                SecureFrameType.UdpBindingGrant,
                payload,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
