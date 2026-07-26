using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.SecureSmoke;

internal sealed class SecureUdpPeer : IAsyncDisposable
{
    private readonly UdpClient _client;
    private readonly SecureUdpProtectedSession _protectedSession;
    private readonly byte[] _connectionId;
    private readonly byte[] _proofKey;
    private ulong _nextInputId = 1;

    private SecureUdpPeer(
        UdpClient client,
        SecureUdpProtectedSession protectedSession,
        byte[] connectionId,
        byte[] proofKey)
    {
        _client = client;
        _protectedSession = protectedSession;
        _connectionId = connectionId;
        _proofKey = proofKey;
    }

    public static async Task<SecureUdpPeer> BindAsync(
        IPAddress address,
        int expectedPort,
        SecureUdpBindingGrant grant,
        CancellationToken cancellationToken)
    {
        if (grant.UdpPort != expectedPort)
        {
            throw new InvalidDataException(
                "TLS UDP grant did not target the configured smoke port.");
        }
        if (grant.ExpiryUnixMilliseconds <=
            checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            throw new InvalidDataException(
                "TLS UDP binding grant was already expired.");
        }

        var connectionId = new byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        var proofKey = new byte[SecureUdpBindingGrant.ProofKeyBytes];
        if (!grant.TryCopySecrets(connectionId, proofKey))
        {
            throw new InvalidDataException(
                "TLS UDP binding grant did not expose its owned secrets.");
        }

        var client = new UdpClient(address.AddressFamily);
        SecureUdpProtectedSession? session = null;
        try
        {
            client.Connect(new IPEndPoint(address, expectedPort));
            session = new SecureUdpProtectedSession(
                SecureUdpPeerRole.Client,
                proofKey,
                connectionId,
                grant.ServerId,
                previousEpochOverlap: TimeSpan.FromSeconds(10));
            var peer = new SecureUdpPeer(
                client,
                session,
                connectionId,
                proofKey);
            await peer.CompleteBindingAsync(cancellationToken);
            return peer;
        }
        catch
        {
            session?.Dispose();
            client.Dispose();
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(proofKey);
            throw;
        }
    }

    public async Task<SecureRealtimePositionSnapshot>
        ReceiveSnapshotAsync(
            Func<SecureRealtimePositionSnapshot, bool> predicate,
            CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var (header, plaintext) =
                await ReceiveProtectedAsync(cancellationToken);
            try
            {
                if (header.MessageType !=
                        SecureUdpProtectedMessageType.PositionSnapshot ||
                    !SecureRealtimeMovementProtocol
                        .TryDecodePositionSnapshot(
                            plaintext,
                            out var snapshot))
                {
                    continue;
                }
                if (predicate(snapshot))
                {
                    return snapshot;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        throw new InvalidDataException(
            "No matching authoritative snapshot arrived within 64 datagrams.");
    }

    public async Task<ulong> SendMovementAsync(
        SecureRealtimePositionSnapshot baseline,
        float x,
        float z,
        CancellationToken cancellationToken)
    {
        var inputId = _nextInputId++;
        var input = new SecureRealtimeMovementInput(
            SecureRealtimeMovementFlags.None,
            baseline.TransportEpoch,
            inputId,
            checked((ulong)Environment.TickCount64 + 1UL),
            baseline.WorldGeneration,
            baseline.LegacyState,
            x,
            z,
            baseline.Auxiliary,
            baseline.MapId);
        var payload = new byte[
            SecureRealtimeMovementProtocol.MovementInputBytes];
        var datagram = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];
        try
        {
            if (!SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                    input,
                    SecureRealtimeTransportSource.Udp,
                    payload,
                    out var payloadBytes) ||
                payloadBytes != payload.Length)
            {
                throw new InvalidDataException(
                    "Could not encode the authoritative movement input.");
            }
            if (!_protectedSession.TryProtect(
                    SecureUdpProtectedMessageType.MovementInput,
                    payload,
                    datagram,
                    out var datagramBytes,
                    out var error))
            {
                throw new InvalidDataException(
                    $"Could not protect movement input ({error}).");
            }
            await _client.SendAsync(
                datagram.AsMemory(0, datagramBytes),
                cancellationToken);
            return inputId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    public ValueTask DisposeAsync()
    {
        _protectedSession.Dispose();
        _client.Dispose();
        CryptographicOperations.ZeroMemory(_connectionId);
        CryptographicOperations.ZeroMemory(_proofKey);
        return ValueTask.CompletedTask;
    }

    private async Task CompleteBindingAsync(
        CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(
            SecureUdpBindingConstants.ClientNonceBytes);
        var hello = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        var proof = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        var tlsProof = new byte[
            SecureUdpBindingConstants.TlsProofTagBytes];
        try
        {
            if (!SecureUdpBindingCodec.TryEncode(
                    SecureUdpBindingType.ClientHello,
                    _connectionId,
                    keyEpoch: 0,
                    sequence: 0,
                    nonce,
                    issuedAtUnixSeconds: 0,
                    ReadOnlySpan<byte>.Empty,
                    hello,
                    out var helloBytes) ||
                helloBytes != hello.Length)
            {
                throw new InvalidDataException(
                    "Could not encode the UDP client hello.");
            }
            await _client.SendAsync(hello, cancellationToken);
            var challenge = await ReceiveExactAsync(
                SecureUdpBindingConstants.DatagramBytes,
                cancellationToken);
            if (!SecureUdpBindingCodec.TryDecode(
                    challenge,
                    out var challengeView) ||
                challengeView.Type !=
                    SecureUdpBindingType.ServerChallenge ||
                !challengeView.ConnectionId.SequenceEqual(
                    _connectionId) ||
                !challengeView.ClientNonce.SequenceEqual(nonce))
            {
                throw new InvalidDataException(
                    "Server returned an invalid UDP address challenge.");
            }
            if (!SecureUdpTlsProofAuthenticator.TryCompute(
                    _proofKey,
                    challenge,
                    tlsProof) ||
                !SecureUdpBindingCodec
                    .TryEncodeAuthenticatedClientProof(
                        challengeView.ConnectionId,
                        challengeView.KeyEpoch,
                        challengeView.Sequence,
                        challengeView.ClientNonce,
                        challengeView.IssuedAtUnixSeconds,
                        tlsProof,
                        challengeView.Authenticator,
                        proof,
                        out var proofBytes) ||
                proofBytes != proof.Length)
            {
                throw new InvalidDataException(
                    "Could not encode the authenticated UDP proof.");
            }
            await _client.SendAsync(proof, cancellationToken);

            var (header, payload) =
                await ReceiveProtectedAsync(cancellationToken);
            try
            {
                if (header.MessageType !=
                        SecureUdpProtectedMessageType.BindingConfirm ||
                    payload.Length !=
                        SecureUdpProtectedConstants
                            .BindingConfirmPayloadBytes ||
                    !payload.AsSpan(0, nonce.Length)
                        .SequenceEqual(nonce) ||
                    BinaryPrimitives.ReadUInt64BigEndian(
                        payload.AsSpan(16)) == 0 ||
                    BinaryPrimitives.ReadUInt64BigEndian(
                        payload.AsSpan(24)) == 0)
                {
                    throw new InvalidDataException(
                        "Server returned an invalid protected binding confirmation.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(hello);
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(tlsProof);
        }
    }

    private async Task<(SecureUdpProtectedHeader Header, byte[] Plaintext)>
        ReceiveProtectedAsync(
            CancellationToken cancellationToken)
    {
        var datagram = await ReceiveExactOrBoundedAsync(
            SecureUdpProtectedConstants.MinimumDatagramBytes,
            SecureUdpProtectedConstants.MaximumDatagramBytes,
            cancellationToken);
        var working = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        try
        {
            if (!_protectedSession.TryUnprotect(
                    datagram,
                    working,
                    out var header,
                    out var payloadBytes,
                    out var error))
            {
                throw new InvalidDataException(
                    $"Server returned an invalid protected UDP datagram ({error}).");
            }
            return (
                header,
                working.AsSpan(0, payloadBytes).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(working);
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    private async Task<byte[]> ReceiveExactAsync(
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        var result = await ReceiveExactOrBoundedAsync(
            expectedBytes,
            expectedBytes,
            cancellationToken);
        return result;
    }

    private async Task<byte[]> ReceiveExactOrBoundedAsync(
        int minimumBytes,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var result = await _client.ReceiveAsync(cancellationToken);
        if (result.Buffer.Length < minimumBytes ||
            result.Buffer.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"UDP datagram length {result.Buffer.Length} is outside " +
                $"the expected {minimumBytes}..{maximumBytes} byte range.");
        }
        return result.Buffer;
    }
}
