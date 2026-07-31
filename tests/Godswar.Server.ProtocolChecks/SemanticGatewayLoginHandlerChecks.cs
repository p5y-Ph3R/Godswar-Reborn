using System.Buffers.Binary;
using System.Net;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Networking;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SemanticGatewayChecks
{
    public const string LoginLifecycleCheckName =
        "B18C2 semantic gateway login lifecycle regressions";

    public static async Task RunLoginLifecycleAsync()
    {
        await CheckCanonicalUsernameCasingAcceptedAsync();
        await CheckPreRedirectDisconnectCancelsGenerationAsync();
        await CheckRedirectFailureCancelsMatchingRelayAsync();
        await CheckGenerationAwareRelayReplacementAsync();
        await CheckTransientDecryptedBodyIsClearedAsync();
    }

    private static async Task
        CheckCanonicalUsernameCasingAcceptedAsync()
    {
        var authority = CreateLoginAuthority();
        using var connections =
            new SemanticGatewayConnectionCoordinator(
                maximumConnections: 4,
                replacementTimeout: TimeSpan.FromSeconds(1));
        await using var data =
            new LoginHandlerDataSession(
                new SemanticGatewayAuthenticatedAccount(
                    7,
                    "test"));
        var inbound = EncryptLoginStream(
            "TEST",
            "password",
            Opcodes.LoginReturnInfo);
        var transport = new ScriptedLegacyByteTransport(
            inbound,
            readChunks: [1, 2, 7, 3],
            remoteEndPoint: "127.0.0.1:41001");
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new SemanticGatewayLoginHandler(
            session,
            data,
            authority,
            connections,
            "127.0.0.1",
            41002);

        await handler.RunAsync(CancellationToken.None);

        Check.Equal(
            1,
            data.AuthenticationCalls,
            "case-insensitive authentication adapter returns one success");
        var snapshot = authority.GetSnapshot();
        Check.True(
            snapshot.ActiveLoginGenerations == 1,
            "client casing preserves one redirected login generation");
        var lookup = authority.TryFindLogin(
            "TEST",
            IPAddress.Loopback);
        Check.True(
            lookup.IsFound &&
            lookup.Generation!.Principal.AccountId == 7 &&
            lookup.Generation.Principal.CanonicalUsername == "test",
            "case-insensitive lookup routes to server-derived canonical " +
            "account identity");
    }

    private static async Task
        CheckPreRedirectDisconnectCancelsGenerationAsync()
    {
        var authority = CreateLoginAuthority();
        using var connections =
            new SemanticGatewayConnectionCoordinator(
                maximumConnections: 4,
                replacementTimeout: TimeSpan.FromSeconds(1));
        await using var data =
            new LoginHandlerDataSession(
                new SemanticGatewayAuthenticatedAccount(
                    7,
                    "TEST"));
        var transport = new ScriptedLegacyByteTransport(
            EncryptLoginStream("TEST", "password"),
            readChunks: [2, 1, 11],
            remoteEndPoint: "127.0.0.1:41003");
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new SemanticGatewayLoginHandler(
            session,
            data,
            authority,
            connections,
            "127.0.0.1",
            41004);

        await handler.RunAsync(CancellationToken.None);

        var snapshot = authority.GetSnapshot();
        Check.True(
            snapshot.LoginGenerationsStarted == 1 &&
            snapshot.ActiveLoginGenerations == 0,
            "disconnect after authentication but before redirect cancels " +
            "the started login generation");
        Check.True(
            authority.TryFindLogin(
                "TEST",
                IPAddress.Loopback).Status ==
                SemanticGatewayLoginLookupStatus.NotFound,
            "cancelled pre-redirect generation cannot enter game");
    }

    private static async Task
        CheckTransientDecryptedBodyIsClearedAsync()
    {
        var clear = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            clear,
            checked((ushort)clear.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            clear.AsSpan(2),
            Opcodes.SelectServer);
        clear.AsSpan(4).Fill(0xA5);
        var encrypted = clear.ToArray();
        new PacketCipher().Transform(encrypted);
        var transport =
            new CapturingPacketBodyTransport(encrypted);
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);

        var packet = await session.ReadPacketAsync(
            CancellationToken.None);

        Check.True(
            packet is not null &&
            packet.Opcode == Opcodes.SelectServer &&
            packet.Payload[0] == 0xA5,
            "client session returns a complete decrypted packet");
        Check.True(
            transport.CapturedBody.Length == clear.Length - 2 &&
            transport.CapturedBody.Span
                .IndexOfAnyExcept((byte)0) < 0,
            "transient decrypted rest buffer is zeroed before read returns");
    }

    private static SemanticGatewayAdmissionAuthority
        CreateLoginAuthority() =>
        new(
            CreateDirectory(),
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 4,
                maximumAdmissions: 4,
                maximumAdmissionsPerGeneration: 1));

    private static byte[] EncryptLoginStream(
        string rawUsername,
        string password,
        params ushort[] followingOpcodes)
    {
        var clear = new byte[
            68 + followingOpcodes.Length * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            clear,
            68);
        BinaryPrimitives.WriteUInt16LittleEndian(
            clear.AsSpan(2),
            Opcodes.Login);
        PacketText.WriteFixedAscii(
            clear.AsSpan(4, 32),
            rawUsername);
        PacketText.WriteFixedAscii(
            clear.AsSpan(36, 32),
            password);
        for (var index = 0;
             index < followingOpcodes.Length;
             index++)
        {
            var offset = 68 + index * 4;
            BinaryPrimitives.WriteUInt16LittleEndian(
                clear.AsSpan(offset),
                4);
            BinaryPrimitives.WriteUInt16LittleEndian(
                clear.AsSpan(offset + 2),
                followingOpcodes[index]);
        }
        new PacketCipher().Transform(clear);
        return clear;
    }

    private sealed class LoginHandlerDataSession(
        SemanticGatewayAuthenticatedAccount authenticated) :
        ISemanticGatewayDataSession
    {
        public int AuthenticationCalls { get; private set; }

        public Task<SemanticGatewayAuthenticatedAccount?>
            AuthenticateAsync(
                string username,
                ReadOnlyMemory<byte> password,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.True(
                username == "TEST" &&
                password.Span.SequenceEqual("password"u8),
                "login handler passes decoded supplied credentials");
            AuthenticationCalls++;
            return Task.FromResult<
                SemanticGatewayAuthenticatedAccount?>(
                authenticated);
        }

        public Task<SemanticGatewayCharacterRoute?>
            FindCharacterRouteAsync(
                int accountId,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Login-only regression must not query character routing.");

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }

    private sealed class CapturingPacketBodyTransport(
        byte[] encrypted) :
        ILegacyByteTransport
    {
        private int _offset;
        private int _readCalls;

        public string RemoteEndPoint => "127.0.0.1:41005";

        public ReadOnlyMemory<byte> CapturedBody { get; private set; }

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= encrypted.Length)
            {
                return ValueTask.FromResult(0);
            }

            _readCalls++;
            var count = Math.Min(
                destination.Length,
                encrypted.Length - _offset);
            encrypted.AsMemory(_offset, count)
                .CopyTo(destination);
            _offset += count;
            if (_readCalls == 3)
            {
                CapturedBody = destination;
            }
            return ValueTask.FromResult(count);
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
