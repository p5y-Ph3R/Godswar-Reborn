using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClassSuitHandlerChecks
{
    private const int GearSlot = 0;
    private const int InsigniaSlot = 1;
    private const long InventoryRevision = 41;
    private static readonly Guid OperationId =
        Guid.Parse("3f9608cd-611e-48ce-a739-358b8670a760");
    private static readonly Guid OutboxEventId =
        Guid.Parse("3adf803a-8cf6-4b9e-9774-168aaf54f9a0");
    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        FindHandlerMethod("InstallNpcCatalog");

    private static readonly CompactItemEntry CommonWeapon =
        CompactItemEntry.Empty with
        {
            Id = 1_013,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };

    private static readonly CompactItemEntry TierOneWeapon =
        CommonWeapon with
        {
            Id = 1_032,
            Bound = 1
        };

    private static readonly CompactItemEntry TierOneInsignia =
        CompactItemEntry.Empty with
        {
            Id = 3_931,
            Quality = 1,
            Grade = 1,
            Stack = 3
        };

    private static async Task<ClassSuitFixture> CreateFixtureAsync(
        bool configureSuccessfulExecution = true)
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var beforeSnapshot = WithInventory(
            baseSnapshot,
            CommonWeapon,
            TierOneInsignia,
            inventoryRevision: InventoryRevision - 1);
        var afterSnapshot = WithInventory(
            baseSnapshot,
            TierOneWeapon,
            CompactItemEntry.Empty,
            InventoryRevision);
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            beforeSnapshot) ?? throw new InvalidOperationException(
                "Class Suit handler fixture did not hydrate.");
        var character = hydrated.Character;
        character.PositionX = 142;
        character.PositionZ = -165;

        var npc = CreateGearMentor(character);
        var worldContent = CreateWorldContent(npc);
        var transport = new PetDurableCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            baseSnapshot.AccountId,
            character);
        var executor = new ClassSuitExecutor(
            configureSuccessfulExecution
                ? CreateSuccessfulReceipt(
                    character.Id,
                    CommonWeapon.ToCompactString(),
                    TierOneWeapon.ToCompactString(),
                    TierOneInsignia.ToCompactString())
                : null);
        var snapshots = new ClassSuitSnapshotReader(afterSnapshot);
        var handler = new GameClientHandler(
            session,
            new ClassSuitGameStore(),
            registry,
            snapshots,
            worldContent,
            classSuitCommands: executor);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(
                baseSnapshot.AccountId,
                "class-suit-handler-check"));
        SetHandlerField(handler, "_character", character);

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap,
            [npc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Class Suit NPC visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Class Suit NPC visibility calculates");
        visibility.Commit(delta);

        return new ClassSuitFixture(
            session,
            transport,
            handler,
            executor,
            snapshots,
            registry,
            character);
    }

    private static CharacterAccountSnapshot WithInventory(
        CharacterAccountSnapshot snapshot,
        CompactItemEntry gear,
        CompactItemEntry insignia,
        long inventoryRevision)
    {
        var character = snapshot.Character ??
            throw new InvalidOperationException(
                "Class Suit fixture requires a character.");
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            gear.ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            InsigniaSlot,
            insignia.ToCompactString());
        return snapshot with
        {
            Character = character with
            {
                Loadout = character.Loadout with
                {
                    KitBag = bag,
                    InventoryRevision = inventoryRevision
                }
            }
        };
    }

    private static NpcSpawnDefinition CreateGearMentor(
        GameCharacter character) =>
        new(
            character.CurrentMap,
            "Sparta",
            "Sparta_070",
            "Sparta_070_Male1",
            ClassSuitProtocol.SpartaNpcId,
            character.PositionX,
            character.PositionZ,
            ClassSuitProtocol.SpartaNpcId,
            AppearanceType: 1,
            Facing: 1.7f,
            Detail10077: [],
            Detail10080: []);

    private static IWorldContentReader CreateWorldContent(
        NpcSpawnDefinition npc) =>
        PinnedWorldContentReader.Create(
            "class-suit-handler-v2",
            [npc.MapId],
            [npc],
            [],
            [],
            new DateTimeOffset(
                2026,
                8,
                2,
                0,
                0,
                0,
                TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Gear Mentor",
                    "Test dialogue")
            ],
            npcDialogueRoutes:
            [
                new NpcDialogueRouteDefinition(
                    npc.NpcKey,
                    npc.NpcKey,
                    GearEnhancerProtocol.DialogIndex,
                    NpcDialogueBehavior.GearMentor,
                    ImmutableArray.Create(1, 2, 3, 4, 5, 6, 7, 8, 9))
                {
                    RouteOrder = 0
                },
                new NpcDialogueRouteDefinition(
                    npc.NpcKey,
                    npc.NpcKey,
                    ClassSuitProtocol.DialogIndex,
                    NpcDialogueBehavior.ClassSuit,
                    ImmutableArray.CreateRange(
                        ClassSuitProtocol.InitialMenuSubIds))
                {
                    RouteOrder = 1
                }
            ]);

    private static ClassSuitExecutionReceipt CreateSuccessfulReceipt(
        int characterId,
        string gearBefore,
        string gearAfter,
        string insigniaBefore) =>
        new(
            CommandFamily.ClassSuitExchangeTierI,
            characterId,
            ClassSuitCommandOperation.ExchangeTierI,
            checked((int)ClassSuitProtocol.SpartaNpcId),
            ClassSuitProtocol.DialogIndex,
            new ClassSuitReplayIntent(
                ClassSuitCommandOperation.ExchangeTierI,
                checked((int)ClassSuitProtocol.SpartaNpcId),
                ClassSuitProtocol.DialogIndex,
                GearSlot,
                InsigniaSlot,
                ClassSuitReplayIntent.NoKitBagSlot),
            ClassSuitCommandResultStatus.Succeeded,
            NativeResultSubId: 120,
            Mutations:
            [
                new ClassSuitReceiptMutation(
                    GearSlot,
                    CommonWeapon.Id,
                    TierOneWeapon.Id,
                    gearBefore,
                    gearAfter),
                new ClassSuitReceiptMutation(
                    InsigniaSlot,
                    TierOneInsignia.Id,
                    0,
                    insigniaBefore,
                    CompactItemEntry.Empty.ToCompactString())
            ],
            InventoryRevision,
            "audit:class-suit:handler-check",
            OutboxEventId);

    private static GamePacket CreateDialogOpenPacket()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            ClassSuitProtocol.SpartaNpcId);
        return new GamePacket(bytes);
    }

    private static GamePacket CreateClassSuitActionPacket(
        int subId,
        Action<int[]>? configure = null,
        Guid? operationId = null,
        uint npcId = ClassSuitProtocol.SpartaNpcId)
    {
        var arguments = Enumerable.Repeat(
            -1,
            ClassSuitProtocol.FunctionArgumentCount).ToArray();
        configure?.Invoke(arguments);
        var bytes = new byte[ClassSuitProtocol.PacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            ClassSuitProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            ClassSuitProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(16),
            subId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new GamePacket(bytes, operationId);
    }

    private static GamePacket CreateTierOneMutationPacket(
        Guid? operationId,
        Action<int[]>? afterSelections = null,
        uint npcId = ClassSuitProtocol.SpartaNpcId) =>
        CreateClassSuitActionPacket(
            (int)ClassSuitWireOperation.ExchangeTierOne,
            arguments =>
            {
                arguments[ClassSuitProtocol.EquipmentArgumentIndex] =
                    ClassSuitProtocol.MinimumKitBagReference + GearSlot;
                arguments[ClassSuitProtocol.MaterialArgumentIndex] =
                    ClassSuitProtocol.MinimumKitBagReference + InsigniaSlot;
                afterSelections?.Invoke(arguments);
            },
            operationId,
            npcId);

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Class Suit handler did not return a task.");
        await task;
    }

    private static MethodInfo FindHandlerMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private static void SetHandlerField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private static T? GetHandlerField<T>(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        return (T?)field.GetValue(handler);
    }

    private sealed record ClassSuitFixture(
        ClientSession Session,
        PetDurableCaptureTransport Transport,
        GameClientHandler Handler,
        ClassSuitExecutor Executor,
        ClassSuitSnapshotReader Snapshots,
        GameSessionRegistry Registry,
        GameCharacter Character) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class ClassSuitExecutor(
        ClassSuitExecutionReceipt? successfulReceipt) :
        IClassSuitCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public CommandEnvelope<ClassSuitCommand>? Envelope { get; private set; }
        public ClassSuitReplayIntent? ReplayIntent { get; private set; }
        public ClassSuitExecutionResult ReplayResult { get; set; } =
            ClassSuitExecutionResult.ReplayNotFound();
        public ClassSuitExecutionReceipt? SuccessfulReceipt =>
            successfulReceipt;

        public Task<ClassSuitExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            ClassSuitReplayIntent replayIntent,
            ClassSuitOperationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            ReplayIntent = replayIntent;
            Check.Equal(7, subject.AccountId, "Class Suit replay account");
            Check.Equal(19, subject.CharacterId, "Class Suit replay character");
            Check.True(identity.IsSecureClient, "Class Suit replay identity");
            Check.Equal(OperationId, identity.OperationId, "Class Suit replay UUID");
            Check.Equal(
                (int)ClassSuitCommandOperation.ExchangeTierI,
                (int)replayIntent.Operation,
                "Class Suit replay operation");
            return Task.FromResult(ReplayResult);
        }

        public Task<ClassSuitExecutionResult> ExecuteAsync(
            CommandEnvelope<ClassSuitCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            Envelope = envelope;
            return Task.FromResult(
                ClassSuitExecutionResult.Committed(
                    successfulReceipt ??
                    throw new InvalidOperationException(
                        "Class Suit execution was not configured.")));
        }
    }

    private sealed class ClassSuitSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Check.Equal(snapshot.AccountId, accountId, "projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ClassSuitGameStore : GameStoreTestStub;
}
