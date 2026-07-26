using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.SecureSmoke;

internal sealed class SecureTlsPeer : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly SecureEndpointRole _role;
    private readonly PacketCipher _receiveCipher = new();
    private readonly PacketCipher _sendCipher = new();
    private ulong _receiveSequence = 1;
    private ulong _sendSequence = 1;

    private SecureTlsPeer(
        TcpClient client,
        SslStream stream,
        SecureEndpointRole role,
        SecureServerPreface preface)
    {
        _client = client;
        _stream = stream;
        _role = role;
        Preface = preface;
    }

    public SecureServerPreface Preface { get; }

    public static async Task<SecureTlsPeer> ConnectAsync(
        IPAddress address,
        int port,
        string targetHost,
        SecureEndpointRole role,
        ReadOnlyMemory<byte> clientInstanceId,
        X509Certificate2 trustedRoot,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily);
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(
                address,
                port,
                cancellationToken);
            stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false);
            await stream.AuthenticateAsClientAsync(
                CreateAuthenticationOptions(
                    targetHost,
                    trustedRoot),
                cancellationToken);
            ValidateNegotiation(stream);

            var prefaceBytes = EncodePreface(
                role,
                clientInstanceId.Span);
            await stream.WriteAsync(
                prefaceBytes,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var response = await ReadExactlyAsync(
                stream,
                SecureProtocolConstants.ServerPrefaceBytes,
                cancellationToken);
            if (!SecurePrefaceCodec.TryDecodeServer(
                    response,
                    role,
                    out var preface) ||
                preface is null ||
                preface.Status != SecureServerPrefaceStatus.Ok)
            {
                throw new InvalidDataException(
                    $"Secure {role} preface was not accepted.");
            }

            return new SecureTlsPeer(
                client,
                stream,
                role,
                preface);
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
            client.Dispose();
            throw;
        }
    }

    public async Task SendLegacyPacketAsync(
        byte[] clearPacket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clearPacket);
        var encrypted = (byte[])clearPacket.Clone();
        try
        {
            _sendCipher.Transform(encrypted);
            await SendFrameAsync(
                SecureFrameType.LegacyBytes,
                encrypted,
                cancellationToken);
        }
        finally
        {
            Array.Clear(encrypted);
        }
    }

    public async Task SendFrameAsync(
        SecureFrameType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length];
        if (!SecureFrameCodec.TryEncode(
                new SecureFrameHeader(
                    checked((uint)payload.Length),
                    type,
                    _sendSequence),
                payload,
                _role,
                SecureFrameDirection.ClientToServer,
                bytes,
                out var written) ||
            written != bytes.Length)
        {
            throw new InvalidDataException(
                $"Could not encode secure {type} frame.");
        }

        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            _sendSequence = checked(_sendSequence + 1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<SecureReceivedFrame> ReadFrameAsync(
        CancellationToken cancellationToken)
    {
        var headerBytes = await ReadExactlyAsync(
            _stream,
            SecureProtocolConstants.FrameHeaderBytes,
            cancellationToken);
        if (!SecureFrameCodec.TryDecodeHeader(
                headerBytes,
                _role,
                SecureFrameDirection.ServerToClient,
                _receiveSequence,
                out var header))
        {
            throw new InvalidDataException(
                "Server sent an invalid secure frame header.");
        }

        var payload = await ReadExactlyAsync(
            _stream,
            checked((int)header.PayloadLength),
            cancellationToken);
        _receiveSequence = checked(_receiveSequence + 1);
        if (header.Type == SecureFrameType.LegacyBytes)
        {
            _receiveCipher.Transform(payload);
        }
        return new SecureReceivedFrame(header.Type, payload);
    }

    public async Task<SecureReceivedFrame> ReadUntilAsync(
        SecureFrameType type,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        for (var count = 0; count < maximumFrames; count++)
        {
            var frame = await ReadFrameAsync(cancellationToken);
            if (frame.Type == type)
            {
                return frame;
            }
            frame.Dispose();
        }

        throw new InvalidDataException(
            $"Server did not send {type} within {maximumFrames} frames.");
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SecureReceivedFrame frame;
            try
            {
                frame = await ReadFrameAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            using (frame)
            {
                if (frame.Type != SecureFrameType.Ping)
                {
                    continue;
                }
                await SendFrameAsync(
                    SecureFrameType.Pong,
                    frame.Payload,
                    cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        finally
        {
            _client.Dispose();
        }
    }

    private static SslClientAuthenticationOptions
        CreateAuthenticationOptions(
            string targetHost,
            X509Certificate2 trustedRoot)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationFlags = X509VerificationFlags.NoFlag
        };
        policy.CustomTrustStore.Add(trustedRoot);
        return new SslClientAuthenticationOptions
        {
            AllowRenegotiation = false,
            ApplicationProtocols =
                [SecureTlsPolicy.ApplicationProtocol],
            CertificateChainPolicy = policy,
            EnabledSslProtocols =
                SslProtocols.Tls12 | SslProtocols.Tls13,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            TargetHost = targetHost
        };
    }

    private static void ValidateNegotiation(SslStream stream)
    {
        if (!stream.IsAuthenticated ||
            !stream.IsEncrypted ||
            !stream.IsSigned ||
            stream.SslProtocol is not (
                SslProtocols.Tls12 or SslProtocols.Tls13) ||
            stream.NegotiatedApplicationProtocol !=
                SecureTlsPolicy.ApplicationProtocol ||
            !SecureTlsPolicy.IsCipherSuiteAllowed(
                stream.NegotiatedCipherSuite))
        {
            throw new AuthenticationException(
                "TLS negotiation did not satisfy the secure-game policy.");
        }
    }

    private static byte[] EncodePreface(
        SecureEndpointRole role,
        ReadOnlySpan<byte> clientInstanceId)
    {
        var buildHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        try
        {
            var preface = new SecureClientPreface(
                role,
                clientInstanceId,
                buildHash);
            var bytes = new byte[
                SecureProtocolConstants.ClientPrefaceBytes];
            if (!SecurePrefaceCodec.TryEncodeClient(
                    preface,
                    bytes,
                    out var written) ||
                written != bytes.Length)
            {
                throw new InvalidDataException(
                    "Could not encode the secure client preface.");
            }
            return bytes;
        }
        finally
        {
            Array.Clear(buildHash);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int bytes,
        CancellationToken cancellationToken)
    {
        var result = new byte[bytes];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = await stream.ReadAsync(
                result.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Secure peer closed after {offset} of {bytes} bytes.");
            }
            offset += read;
        }
        return result;
    }
}

internal sealed class SecureReceivedFrame(
    SecureFrameType type,
    byte[] payload) : IDisposable
{
    public SecureFrameType Type { get; } = type;

    public byte[] Payload { get; } = payload;

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Payload);
    }
}
