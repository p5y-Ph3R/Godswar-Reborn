using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class LegacyRealmLoginProtocolChecks
{
    private static async Task CheckGameHandlerRealmAdmissionAsync(
        RealmCatalogEntry tempest,
        RealmCatalogEntry dwargon,
        RealmCatalogSnapshot catalog,
        ServerOptions options)
    {
        var acceptedReader = new FixedRealmCatalogReader(catalog);
        var acceptedTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            acceptedTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var handler = CreateGameHandler(
                session,
                new RealmAdmissionGameStore(),
                acceptedReader,
                dwargon.RealmId,
                options);
            var username = await ResolveGameUsernameAsync(
                handler,
                GameLoginPacket(dwargon));
            Check.True(
                string.Equals(
                    "test2",
                    username,
                    StringComparison.Ordinal),
                "hosted game worker accepts its enabled realm token");
        }
        Check.Equal(
            1,
            acceptedReader.ReadCount,
            "hosted game admission reads the current enabled catalog");
        Check.Equal(
            0,
            acceptedTransport.DisconnectCount,
            "matching game realm admission remains connected");

        var wrongRealmReader = new FixedRealmCatalogReader(catalog);
        var wrongRealmStore = new RealmAdmissionGameStore();
        var wrongRealmTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            wrongRealmTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var handler = CreateGameHandler(
                session,
                wrongRealmStore,
                wrongRealmReader,
                dwargon.RealmId,
                options);
            await InvokeGameLoginAsync(
                handler,
                GameLoginPacket(tempest));
        }
        Check.Equal(
            1,
            wrongRealmTransport.DisconnectCount,
            "hosted game worker rejects a different realm ID");
        Check.Equal(
            0,
            wrongRealmReader.ReadCount,
            "cross-realm claim fails before catalog access");
        Check.Equal(
            0,
            wrongRealmStore.FindByUsernameCalls,
            "cross-realm claim fails before account lookup");

        var wrongToken = Entry(
            dwargon.RealmId,
            dwargon.Name,
            "BAD3jcIzqGgKvOf1dbYZKC8cS",
            dwargon.Host,
            dwargon.Recommended,
            dwargon.DisplayOrder);
        var wrongTokenReader = new FixedRealmCatalogReader(catalog);
        var wrongTokenStore = new RealmAdmissionGameStore();
        var wrongTokenTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            wrongTokenTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var handler = CreateGameHandler(
                session,
                wrongTokenStore,
                wrongTokenReader,
                dwargon.RealmId,
                options);
            await InvokeGameLoginAsync(
                handler,
                GameLoginPacket(wrongToken));
        }
        Check.Equal(
            1,
            wrongTokenTransport.DisconnectCount,
            "hosted game worker rejects a mismatched routing token");
        Check.Equal(
            1,
            wrongTokenReader.ReadCount,
            "routing token is matched against current catalog state");
        Check.Equal(
            0,
            wrongTokenStore.FindByUsernameCalls,
            "bad routing token fails before account lookup");

        var disabledReader = new FixedRealmCatalogReader(
            new RealmCatalogSnapshot([tempest]));
        var disabledStore = new RealmAdmissionGameStore();
        var disabledTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            disabledTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var handler = CreateGameHandler(
                session,
                disabledStore,
                disabledReader,
                dwargon.RealmId,
                options);
            await InvokeGameLoginAsync(
                handler,
                GameLoginPacket(dwargon));
        }
        Check.Equal(
            1,
            disabledTransport.DisconnectCount,
            "hosted game worker rejects a disabled process realm");
        Check.Equal(
            0,
            disabledStore.FindByUsernameCalls,
            "disabled realm fails before account lookup");
    }

    private static GameClientHandler CreateGameHandler(
        ClientSession session,
        RealmAdmissionGameStore store,
        IRealmCatalogReader catalog,
        RealmId processRealmId,
        ServerOptions options) =>
        new(
            session,
            store,
            new GameSessionRegistry(store),
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess:
                LegacyAuthenticationAccess.Create(
                    ServerRuntimeProfilePolicy.Validate(options)),
            realmCatalog: catalog,
            processRealmId: processRealmId);

    private static byte[] GameLoginPacket(RealmCatalogEntry realm)
    {
        var packet = new byte[LegacyGameLoginPacket.PacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            LegacyGameLoginPacket.PacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.LoginGameServer);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                LegacyGameLoginPacket.UsernameOffset,
                LegacyGameLoginPacket.UsernameLength),
            "test2");
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                LegacyGameLoginPacket.IdentifierOffset,
                LegacyGameLoginPacket.IdentifierLength),
            realm.Identifier);
        packet[LegacyGameLoginPacket.RealmIdOffset] =
            realm.LegacyWireId;
        return packet;
    }

    private static async Task<string?> ResolveGameUsernameAsync(
        GameClientHandler handler,
        byte[] packetBytes)
    {
        var method = typeof(GameClientHandler).GetMethod(
            "ResolveGameLoginUsernameAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "GameClientHandler realm resolver was not found.");
        var invocation = method.Invoke(
            handler,
            [new GamePacket(packetBytes), CancellationToken.None]);
        return await (Task<string?>)(invocation ??
            throw new InvalidOperationException(
                "GameClientHandler realm resolver returned no task."));
    }

    private static async Task InvokeGameLoginAsync(
        GameClientHandler handler,
        byte[] packetBytes)
    {
        var method = typeof(GameClientHandler).GetMethod(
            "HandleGameLoginAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "GameClientHandler login method was not found.");
        var invocation = method.Invoke(
            handler,
            [new GamePacket(packetBytes), CancellationToken.None]);
        await (Task)(invocation ??
            throw new InvalidOperationException(
                "GameClientHandler login method returned no task."));
    }

    private sealed class RealmAdmissionGameStore : GameStoreTestStub
    {
        public int FindByUsernameCalls { get; private set; }

        public override Task<GameAccount?> FindAccountByUsernameAsync(
            string username,
            CancellationToken cancellationToken = default)
        {
            FindByUsernameCalls++;
            return Task.FromResult<GameAccount?>(new GameAccount
            {
                Id = 7,
                Username = username
            });
        }
    }
}
