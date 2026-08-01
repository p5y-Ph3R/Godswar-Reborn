using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class LegacyAuthenticationProfileChecks
{
    public static async Task RunAsync()
    {
        await CheckRawLoginCapabilityAsync();
        await CheckRawGameBindingCapabilityAsync();
        await CheckSecureGameWithoutPrincipalAsync();
    }

    private static async Task CheckRawLoginCapabilityAsync()
    {
        var packet = LoginPacket(
            Opcodes.Login,
            "test2",
            "password",
            68);
        var allowedStore = new CountingAccountStore();
        var allowedTransport =
            new ScriptedLegacyByteTransport();
        var allowedPacketBytes = (byte[])packet.Clone();
        await using (var session = new ClientSession(
            allowedTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var options = LocalOptions();
            var handler = new LoginClientHandler(
                session,
                allowedStore,
                options,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleLoginAsync",
                new GamePacket(allowedPacketBytes));
        }

        Check.Equal(
            1,
            allowedStore.LoginOrCreateCalls,
            "explicit local raw login reaches account upsert once");
        Check.True(
            allowedPacketBytes.AsSpan(36, 32)
                .IndexOfAnyExcept((byte)0) < 0,
            "raw rollback credential packet bytes are cleared");

        var blockedStore = new CountingAccountStore();
        var blockedTransport =
            new ScriptedLegacyByteTransport();
        var blockedPacketBytes = (byte[])packet.Clone();
        await using (var session = new ClientSession(
            blockedTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                blockedStore,
                LocalOptions());
            await InvokeAsync(
                handler,
                "HandleLoginAsync",
                new GamePacket(blockedPacketBytes));
        }

        Check.Equal(
            0,
            blockedStore.LoginOrCreateCalls,
            "raw login without local capability performs no account call");
        Check.Equal(
            1,
            blockedTransport.DisconnectCount,
            "raw login without local capability disconnects");
        Check.True(
            blockedPacketBytes.AsSpan(36, 32)
                .IndexOfAnyExcept((byte)0) < 0,
            "blocked raw credential packet bytes are cleared");
    }

    private static async Task CheckRawGameBindingCapabilityAsync()
    {
        var packet = LoginPacket(
            Opcodes.LoginGameServer,
            "test2",
            null,
            36);
        var allowedStore = new CountingAccountStore();
        var allowedTransport =
            new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            allowedTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var options = LocalOptions();
            var registry =
                new GameSessionRegistry(allowedStore);
            var handler = new GameClientHandler(
                session,
                allowedStore,
                registry,
                CharacterSnapshotReaderTestFixtures.Empty,
                WorldContentReaderTestFixtures.Empty,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleGameLoginAsync",
                new GamePacket((byte[])packet.Clone()));
        }

        Check.Equal(
            1,
            allowedStore.FindByUsernameCalls,
            "explicit local raw game bind performs one username lookup");

        var blockedStore = new CountingAccountStore();
        var blockedTransport =
            new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            blockedTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var handler = new GameClientHandler(
                session,
                blockedStore,
                new GameSessionRegistry(blockedStore),
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty);
            await InvokeAsync(
                handler,
                "HandleGameLoginAsync",
                new GamePacket((byte[])packet.Clone()));
        }

        Check.Equal(
            0,
            blockedStore.FindByUsernameCalls,
            "raw game bind without local capability performs no lookup");
        Check.Equal(
            1,
            blockedTransport.DisconnectCount,
            "raw game bind without local capability disconnects");
    }

    private static async Task CheckSecureGameWithoutPrincipalAsync()
    {
        var instanceId = Enumerable.Repeat(
                (byte)0x55,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .ToArray();
        var buildHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        var context = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            instanceId,
            instanceId,
            buildHash);
        var transport = new ScriptedSecureControlTransport(
            context,
            [],
            boundGamePrincipal: null);
        var store = new CountingAccountStore();
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var options = LocalOptions();
            var handler = new GameClientHandler(
                session,
                store,
                new GameSessionRegistry(store),
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleGameLoginAsync",
                new GamePacket(
                    LoginPacket(
                        Opcodes.LoginGameServer,
                        "test2",
                        null,
                        36)));
        }

        Check.Equal(
            0,
            store.FindByUsernameCalls,
            "secure game channel never falls back to username lookup");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "secure game channel without a principal disconnects");
    }

    private static ServerOptions LocalOptions() =>
        new()
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=127.0.0.1;Database=legacy-auth-check"
            },
            Authentication = new AuthenticationOptions
            {
                AllowLegacyRawAuthentication = true
            }
        };

    private static LegacyAuthenticationAccess LocalAccess(
        ServerOptions options) =>
        LegacyAuthenticationAccess.Create(
            ServerRuntimeProfilePolicy.Validate(options)) ??
        throw new InvalidOperationException(
            "Local compatibility capability was not created.");

    private static byte[] LoginPacket(
        ushort opcode,
        string username,
        string? password,
        int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, 32),
            username);
        if (password is not null)
        {
            PacketText.WriteFixedAscii(
                packet.AsSpan(36, 32),
                password);
        }
        return packet;
    }

    private static async Task InvokeAsync(
        object handler,
        string methodName,
        GamePacket packet)
    {
        var method = handler.GetType().GetMethod(
            methodName,
            BindingFlags.Instance |
            BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"{methodName} test hook was not found.");
        var invocation = method.Invoke(
            handler,
            [packet, CancellationToken.None]);
        await (invocation as Task ??
            throw new InvalidOperationException(
                $"{methodName} did not return a task."));
    }

    private sealed class CountingAccountStore :
        GameStoreTestStub
    {
        public int FindByUsernameCalls { get; private set; }

        public int LoginOrCreateCalls { get; private set; }

        public override Task<GameAccount>
            LoginOrCreateAccountAsync(
                string username,
                string password,
                CancellationToken cancellationToken = default)
        {
            LoginOrCreateCalls++;
            return Task.FromResult(Account(username));
        }

        public override Task<GameAccount?>
            FindAccountByUsernameAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            FindByUsernameCalls++;
            return Task.FromResult<GameAccount?>(
                Account(username));
        }

        public override Task<GameCharacter?>
            GetFirstCharacterAsync(
                int accountId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<GameCharacter?>(null);

        private static GameAccount Account(string username) =>
            new()
            {
                Id = 7,
                Username = username
            };
    }
}
