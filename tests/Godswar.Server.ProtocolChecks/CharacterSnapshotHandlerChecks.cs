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
        var source = CharacterSnapshotContractChecks
            .CreateUnmergedValidSnapshot();
        var sourceCharacter = source.Character ??
            throw new InvalidOperationException(
                "The handler fixture requires a character.");
        source = source with
        {
            Character = sourceCharacter with
            {
                Loadout = sourceCharacter.Loadout with
                {
                    Equipment = EquipmentSlots.SetSlot(
                        sourceCharacter.Loadout.Equipment,
                        sourceCharacter.Appearance.Profession,
                        EquipmentSlots.Stylish,
                        "[8068,,,,,,1,1,1,1,0]")
                }
            }
        };
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
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=127.0.0.1;Database=snapshot-handler-check"
            }
        };
        options.Authentication.AllowLegacyRawAuthentication = true;
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
            legacyAuthenticationAccess: legacyAccess,
            characterCheckpoints:
                new GameHandlerCheckpointCoordinatorStub(
                    source.Character!.Location.PositionRevision,
                    source.Character.Vitals.Revision),
            itemContent: TestItemContent.Content,
            petContent: PetContentTestCatalog.Instance);

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

        var detailPacket = PacketBuilder.PlayerDetail(hydrated.Character);
        var beforeCompatibilityRequests = Decrypt(
            transport.WrittenBytes);
        var detailCountBefore = CountOccurrences(
            beforeCompatibilityRequests,
            detailPacket);

        await InvokePacketAsync(
            handler,
            CreatePacket(Opcodes.PlayerDetailRequest, payloadLength: 8));
        await InvokePacketAsync(
            handler,
            CreatePacket(Opcodes.PlayerDetailRequest, payloadLength: 8));

        var afterDetailRequests = Decrypt(transport.WrittenBytes);
        Check.Equal(
            detailCountBefore + 2,
            CountOccurrences(afterDetailRequests, detailPacket),
            "every in-world 10200 request receives PlayerDetail compatibility response");

        var effectsDisabled = PacketBuilder.EquipmentEffectVisibility(
            0x0000_1448u,
            visible: false);
        var disabledEffectsBefore = CountOccurrences(
            afterDetailRequests,
            effectsDisabled);
        await InvokePacketAsync(
            handler,
            CreatePacket(Opcodes.FashionEffectVisibility, payloadLength: 12));
        await InvokePacketAsync(
            handler,
            CreatePacket(Opcodes.FashionEffectVisibility, payloadLength: 12));

        var afterEffectRequests = Decrypt(transport.WrittenBytes);
        Check.Equal(
            disabledEffectsBefore + 2,
            CountOccurrences(afterEffectRequests, effectsDisabled),
            "every valid 10202 request receives one compatibility response, including duplicates");

        Check.Equal(
            2,
            snapshotReader.ReadCount,
            "login and post-fence refresh use two consistent snapshots");
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
        var ownedPetBootstrap = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            hydrated.Pets,
            hydrated.PetShed.OpenedCellCount);
        Check.True(
            Contains(clearBytes, ownedPetBootstrap),
            "the client receives snapshot-backed owned pets");
        var summonedPet = hydrated.Pets.Single(static pet =>
            pet.IsCarried && pet.IsSummoned);
        var restoredPetCallOut = PacketBuilder.PetOperationResult(
            checked((uint)summonedPet.PetId),
            PetOperationResultCode.CallOutSucceeded);
        var restoredPetPresence = PacketBuilder.PetWorldPresence(
            checked((uint)summonedPet.PetId),
            0x0000_1448u);
        var restoredOwnerMerge = PacketBuilder.PetOwnerMergeStarted(
            0x0000_1448u);
        Check.Equal(
            1,
            CountOccurrences(clearBytes, restoredPetCallOut),
            "ordinary login restores the carried companion call-out");
        Check.Equal(
            1,
            CountOccurrences(clearBytes, restoredPetPresence),
            "ordinary login restores one companion model");
        Check.Equal(
            0,
            CountOccurrences(clearBytes, restoredOwnerMerge),
            "ordinary login does not fabricate a native unite presentation");
        var ownedPetIndex = clearBytes.AsSpan().IndexOf(ownedPetBootstrap);
        var callOutIndex = clearBytes.AsSpan().IndexOf(restoredPetCallOut);
        Check.True(
            ownedPetIndex >= 0 &&
            callOutIndex > ownedPetIndex,
            "owned-pet bootstrap precedes companion restoration");
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
        var fashionAppearance = PacketBuilder.EquipmentVisualRefresh(
            hydrated.Character,
            TestItemContent.Content.FashionAppearances);
        var fashionEffects = PacketBuilder.EquipmentEffectVisibility(
            0x0000_1448u,
            GameClientHandler.ResolveEquipmentEffectProjection(
                hydrated.Character));
        Check.True(
            Contains(
                clearBytes,
                fashionAppearance.Concat(fashionEffects).ToArray()),
            "login detail sends self Fashion appearance immediately before effects");
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
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=127.0.0.1;Database=snapshot-handler-check"
            }
        };
        options.Authentication.AllowLegacyRawAuthentication = true;
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

    private static byte[] Decrypt(byte[] encrypted)
    {
        new PacketCipher().Transform(encrypted);
        return encrypted;
    }

    private static int CountOccurrences(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > source.Length)
        {
            return 0;
        }

        var count = 0;
        for (var index = 0;
             index <= source.Length - value.Length;
             index++)
        {
            if (source.Slice(index, value.Length).SequenceEqual(value))
            {
                count++;
            }
        }

        return count;
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
