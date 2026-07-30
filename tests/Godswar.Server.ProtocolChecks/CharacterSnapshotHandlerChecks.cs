using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Operations;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotHandlerChecks
{
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    public static async Task RunAsync()
    {
        await CheckCompleteBootstrapAsync();
        await CheckSnapshotFailureIsFailClosedAsync();
        await CheckPriorSessionIsDisconnectedBeforeSnapshotAsync();
        await CheckCancelledSnapshotCleansAccountSessionAsync();
        await CheckOccupiedSlotRejectsCreateAsync();
        await CheckReplacedSessionCannotStealCheckpointOwnershipAsync();
    }

    private static async Task CheckCompleteBootstrapAsync()
    {
        var source = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(source) ??
            throw new InvalidOperationException(
                "The handler fixture did not hydrate.");
        var snapshotReader = new CountingSnapshotReader(source);
        var store = new FanOutRejectingStore(source.AccountId);
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);
        var options = new ServerOptions
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions { Provider = "Json" }
        };
        var legacyAccess = LegacyAuthenticationAccess.Create(
            ServerRuntimeProfilePolicy.Validate(options)) ??
            throw new InvalidOperationException(
                "The local authentication capability was not created.");
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: legacyAccess);

        await InvokePacketAsync(
            handler,
            CreateLoginPacket("snapshot-user"));
        await InvokePacketAsync(handler, CreatePacket(Opcodes.RoleInfo));
        await InvokePacketAsync(handler, CreatePacket(Opcodes.RoleInfo));
        await InvokePacketAsync(handler, CreatePacket(Opcodes.EnterGame));
        await InvokePacketAsync(handler, CreatePacket(Opcodes.ClientReady));
        await InvokePacketAsync(
            handler,
            CreatePacket(Opcodes.PlayerDetailRequest, payloadLength: 8));
        await InvokePacketAsync(handler, CreatePacket(Opcodes.EnterUiReady));

        Check.Equal(
            1,
            snapshotReader.ReadCount,
            "login, repeated preview, enter, and ready use one snapshot query");
        Check.Equal(
            0,
            store.LegacyCharacterReadCount,
            "initial bootstrap performs no broad-store character fan-out");
        Check.Equal(
            0,
            transport.DisconnectCount,
            "a valid consistent snapshot completes the client bootstrap");

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        Check.True(
            Contains(
                clearBytes,
                PacketBuilder.CharacterPreview(hydrated.Character)),
            "the client receives the snapshot-backed character preview");
        Check.True(
            Contains(
                clearBytes,
                PacketBuilder.OwnedPetList(hydrated.Pets)),
            "the client receives snapshot-backed owned pets");
        Check.True(
            Contains(
                clearBytes,
                PacketBuilder.TalentRankList(hydrated.Talents)),
            "the client receives snapshot-backed talent ranks");
        Check.True(
            Contains(
                clearBytes,
                PacketBuilder.SkillList(hydrated.Skills)),
            "the client receives snapshot-backed learned skills");
    }

    private static async Task CheckSnapshotFailureIsFailClosedAsync()
    {
        const int accountId = 7;
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var store = new FanOutRejectingStore(accountId);
        var options = new ServerOptions
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions { Provider = "Json" }
        };
        var legacyAccess = LegacyAuthenticationAccess.Create(
            ServerRuntimeProfilePolicy.Validate(options)) ??
            throw new InvalidOperationException(
                "The local authentication capability was not created.");
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(store: null),
            new RejectingSnapshotReader(),
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: legacyAccess);

        await InvokePacketAsync(
            handler,
            CreateLoginPacket("snapshot-user"));

        Check.Equal(
            1,
            transport.DisconnectCount,
            "invalid snapshot disconnects before character selection");
        Check.Equal(
            0,
            transport.WrittenBytes.Length,
            "invalid snapshot sends no partial AfterLogin or preview");
    }

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        try
        {
            var task = (Task?)HandlePacketMethod.Invoke(
                handler,
                [packet, CancellationToken.None]) ??
                throw new InvalidOperationException(
                    "HandlePacketAsync returned no task.");
            await task;
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static GamePacket CreateLoginPacket(string username)
    {
        var buffer = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer,
            checked((ushort)buffer.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(2),
            Opcodes.LoginGameServer);
        PacketText.WriteFixedAscii(buffer.AsSpan(4, 32), username);
        return new GamePacket(buffer);
    }

    private static GamePacket CreatePacket(
        ushort opcode,
        int payloadLength = 0)
    {
        var buffer = new byte[4 + payloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer,
            checked((ushort)buffer.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(2),
            opcode);
        return new GamePacket(buffer);
    }

    private static bool Contains(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > source.Length)
        {
            return false;
        }

        for (var index = 0;
             index <= source.Length - value.Length;
             index++)
        {
            if (source.Slice(index, value.Length).SequenceEqual(value))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CountingSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "snapshot query uses authenticated account identity");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RejectingSnapshotReader :
        ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CharacterAccountSnapshot>(
                new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.AmbiguousCharacterSlot,
                    "Synthetic ambiguous-slot failure."));
    }

    private sealed class FanOutRejectingStore(int accountId) :
        GameStoreTestStub
    {
        public int LegacyCharacterReadCount { get; private set; }

        public int CreateCharacterCalls { get; private set; }

        public override Task<GameAccount?> FindAccountByUsernameAsync(
            string username,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameAccount?>(
                new GameAccount
                {
                    Id = accountId,
                    Username = username
                });

        public override Task<GameCharacter?> GetFirstCharacterAsync(
            int requestedAccountId,
            CancellationToken cancellationToken = default) =>
            Rejected<GameCharacter?>();

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int requestedAccountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
            Rejected<CharacterStats?>();

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int requestedAccountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Rejected<IReadOnlyList<SkillState>>();

        public override Task<IReadOnlyList<TalentState>>
            GetTalentStatesAsync(
                int requestedAccountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Rejected<IReadOnlyList<TalentState>>();

        public override Task<IReadOnlyList<PetBootstrapSnapshot>>
            GetOwnedPetsAsync(
                int requestedAccountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Rejected<IReadOnlyList<PetBootstrapSnapshot>>();

        public override Task<WorldBossRespawnState?>
            GetActiveWorldBossRespawnAsync(
                short mapId,
                DateTimeOffset now,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldBossRespawnState?>(null);

        public override Task<GameCharacter> CreateCharacterAsync(
            int requestedAccountId,
            GameCharacter character,
            CancellationToken cancellationToken = default)
        {
            CreateCharacterCalls++;
            return Task.FromException<GameCharacter>(
                new InvalidOperationException(
                    "Occupied-slot CreateRole reached the store."));
        }

        private Task<T> Rejected<T>()
        {
            LegacyCharacterReadCount++;
            return Task.FromException<T>(
                new InvalidOperationException(
                    "Legacy character fan-out was invoked."));
        }
    }
}
