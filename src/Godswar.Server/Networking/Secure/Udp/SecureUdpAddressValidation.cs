using System.Net;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpAddressValidation : IDisposable
{
    private readonly int _maximumDatagramBytes;
    private readonly SecureUdpCookieProtector _cookies;
    private bool _disposed;

    public SecureUdpAddressValidation(
        int maximumDatagramBytes,
        SecureUdpCookieProtector cookies)
    {
        if (maximumDatagramBytes is <
                SecureUdpBindingConstants.DatagramBytes or >
                SecureUdpBindingConstants.MaximumDatagramBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDatagramBytes));
        }

        _maximumDatagramBytes = maximumDatagramBytes;
        _cookies = cookies ??
            throw new ArgumentNullException(nameof(cookies));
    }

    public static bool TryEncodeClientHello(
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> clientNonce,
        Span<byte> destination,
        out int bytesWritten)
    {
        return SecureUdpBindingCodec.TryEncode(
            SecureUdpBindingType.ClientHello,
            connectionId,
            0,
            0,
            clientNonce,
            0,
            ReadOnlySpan<byte>.Empty,
            destination,
            out bytesWritten);
    }

    public bool TryCreateChallenge(
        ReadOnlySpan<byte> clientHello,
        IPEndPoint remoteEndpoint,
        Span<byte> destination,
        out int bytesWritten)
    {
        ThrowIfDisposed();
        bytesWritten = 0;
        if (clientHello.Length > _maximumDatagramBytes ||
            !SecureUdpBindingCodec.TryDecode(
                clientHello,
                out var hello) ||
            hello.Type != SecureUdpBindingType.ClientHello ||
            destination.Length <
                SecureUdpBindingConstants.DatagramBytes)
        {
            return false;
        }

        Span<byte> connectionId = stackalloc byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        Span<byte> nonce = stackalloc byte[
            SecureUdpBindingConstants.ClientNonceBytes];
        Span<byte> tag = stackalloc byte[
            SecureUdpBindingConstants.CookieTagBytes];
        try
        {
            hello.ConnectionId.CopyTo(connectionId);
            hello.ClientNonce.CopyTo(nonce);
            if (!_cookies.TryIssue(
                    remoteEndpoint,
                    connectionId,
                    nonce,
                    out var keyEpoch,
                    out var issuedAt,
                    tag) ||
                !SecureUdpBindingCodec.TryEncode(
                    SecureUdpBindingType.ServerChallenge,
                    connectionId,
                    keyEpoch,
                    0,
                    nonce,
                    issuedAt,
                    tag,
                    destination,
                    out bytesWritten) ||
                bytesWritten > clientHello.Length)
            {
                bytesWritten = 0;
                return false;
            }
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public static bool TryCreateClientProof(
        ReadOnlySpan<byte> serverChallenge,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (!SecureUdpBindingCodec.TryDecode(
                serverChallenge,
                out var challenge) ||
            challenge.Type != SecureUdpBindingType.ServerChallenge)
        {
            return false;
        }

        Span<byte> connectionId = stackalloc byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        Span<byte> nonce = stackalloc byte[
            SecureUdpBindingConstants.ClientNonceBytes];
        Span<byte> tag = stackalloc byte[
            SecureUdpBindingConstants.CookieTagBytes];
        try
        {
            challenge.ConnectionId.CopyTo(connectionId);
            challenge.ClientNonce.CopyTo(nonce);
            challenge.Authenticator.CopyTo(tag);
            return SecureUdpBindingCodec.TryEncode(
                SecureUdpBindingType.ClientProof,
                connectionId,
                challenge.KeyEpoch,
                challenge.Sequence,
                nonce,
                challenge.IssuedAtUnixSeconds,
                tag,
                destination,
                out bytesWritten);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public bool TryValidateClientProof(
        ReadOnlySpan<byte> clientProof,
        IPEndPoint remoteEndpoint,
        Span<byte> connectionIdDestination)
    {
        ThrowIfDisposed();
        if (clientProof.Length > _maximumDatagramBytes ||
            connectionIdDestination.Length <
                SecureUdpBindingConstants.ConnectionIdBytes ||
            !SecureUdpBindingCodec.TryDecode(
                clientProof,
                out var proof) ||
            proof.Type != SecureUdpBindingType.ClientProof ||
            !_cookies.Validate(
                remoteEndpoint,
                proof.ConnectionId,
                proof.ClientNonce,
                proof.KeyEpoch,
                proof.IssuedAtUnixSeconds,
                proof.Authenticator))
        {
            return false;
        }

        proof.ConnectionId.CopyTo(connectionIdDestination);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cookies.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
