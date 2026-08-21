using System.Buffers.Binary;
using System.Net;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
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
        await CheckDisabledRealmSelectionRejectedAsync();
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
            SelectRealmPacket(RealmId.Dwargon),
            OpcodePacket(Opcodes.LoginReturnInfo));
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
            CreateLoginCoordination(authority),
            connections,
            TimeSpan.FromSeconds(1));

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
            lookup.Generation.Principal.CanonicalUsername == "test" &&
            lookup.Generation.RealmGrant ==
                SemanticGatewayTestRealm.DwargonGrant,
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
            EncryptLoginStream(
                "TEST",
                "password",
                SelectRealmPacket(RealmId.Tempest)),
            readChunks: [2, 1, 11],
            remoteEndPoint: "127.0.0.1:41003");
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new SemanticGatewayLoginHandler(
            session,
            data,
            CreateLoginCoordination(authority),
            connections,
            TimeSpan.FromSeconds(1));

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

    private static async Task CheckDisabledRealmSelectionRejectedAsync()
    {
        var authority = CreateLoginAuthority();
        using var connections =
            new SemanticGatewayConnectionCoordinator(
                maximumConnections: 4,
                replacementTimeout: TimeSpan.FromSeconds(1));
        await using var data = new LoginHandlerDataSession(
            new SemanticGatewayAuthenticatedAccount(7, "TEST"),
            SemanticGatewayTestRealm.Catalog,
            new RealmCatalogSnapshot([SemanticGatewayTestRealm.Tempest]));
        var transport = new ScriptedLegacyByteTransport(
            EncryptLoginStream(
                "TEST",
                "password",
                SelectRealmPacket(RealmId.Dwargon)),
            remoteEndPoint: "127.0.0.1:41007");
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new SemanticGatewayLoginHandler(
            session,
            data,
            CreateLoginCoordination(authority),
            connections,
            TimeSpan.FromSeconds(1));

        await handler.RunAsync(CancellationToken.None);

        Check.True(
            transport.DisconnectCount == 1 &&
            authority.GetSnapshot().LoginGenerationsStarted == 0,
            "a realm disabled after advertisement cannot mint a login grant");
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

    private static ISemanticGatewayCoordination
        CreateLoginCoordination(
            SemanticGatewayAdmissionAuthority authority) =>
        new InMemorySemanticGatewayCoordination(authority);

    private static byte[] EncryptLoginStream(
        string rawUsername,
        string password,
        params byte[][] followingPackets)
    {
        var clear = new byte[
            68 + followingPackets.Sum(static packet => packet.Length)];
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
        var offset = 68;
        foreach (var following in followingPackets)
        {
            following.CopyTo(clear, offset);
            offset += following.Length;
        }
        new PacketCipher().Transform(clear);
        return clear;
    }

    private static byte[] OpcodePacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        return packet;
    }

    private static byte[] SelectRealmPacket(RealmId realmId)
    {
        var packet = new byte[LegacyRealmSelectionPacket.PacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SelectServer);
        packet[LegacyRealmSelectionPacket.RealmIdOffset] =
            checked((byte)realmId.Value);
        return packet;
    }

    private sealed class LoginHandlerDataSession(
        SemanticGatewayAuthenticatedAccount authenticated,
        params RealmCatalogSnapshot[] catalogs) :
        ISemanticGatewayDataSession
    {
        private readonly RealmCatalogSnapshot[] _catalogs =
            catalogs.Length == 0
                ? [SemanticGatewayTestRealm.Catalog]
                : catalogs;
        private int _catalogReadCount;

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
                RealmId realmId,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Login-only regression must not query character routing.");

        public Task<RealmCatalogSnapshot> ReadEnabledAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(
                Interlocked.Increment(ref _catalogReadCount) - 1,
                _catalogs.Length - 1);
            return Task.FromResult(_catalogs[index]);
        }

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
