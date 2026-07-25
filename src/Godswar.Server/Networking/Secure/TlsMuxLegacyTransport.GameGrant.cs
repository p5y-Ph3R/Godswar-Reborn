using System.Buffers;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    public async ValueTask SendGameGrantAsync(
        SecureGameGrant grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (_secureRole != SecureEndpointRole.Login ||
            _boundGamePrincipal is not null)
        {
            throw new InvalidOperationException(
                "A game grant can be sent only by an authenticated secure login channel.");
        }
        if (!_authenticated.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "A game grant cannot be sent before login authentication.");
        }
        if (Interlocked.Exchange(ref _gameGrantStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A secure login channel may send only one game grant.");
        }

        var payload = ArrayPool<byte>.Shared.Rent(
            SecureProtocolConstants.MaximumGameGrantBytes);
        var bytesWritten = 0;
        try
        {
            if (!SecureGameControlCodec.TryEncodeGrant(
                    grant,
                    payload,
                    out bytesWritten))
            {
                throw new InvalidOperationException(
                    "The bounded game grant could not be encoded.");
            }

            await WriteControlFrameAsync(
                SecureFrameType.GameGrant,
                payload.AsMemory(0, bytesWritten),
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                payload.AsSpan(0, Math.Max(0, bytesWritten)));
            ArrayPool<byte>.Shared.Return(payload);
        }
    }
}
