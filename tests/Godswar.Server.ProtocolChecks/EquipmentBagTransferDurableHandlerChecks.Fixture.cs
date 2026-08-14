using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private const int EquipmentSlot = EquipmentSlots.Weapon;
    private const int KitBagSlot = 25;
    private const int PersistedPhysicalAttack = 91_337;
    private static readonly Guid OperationId =
        Guid.Parse("abfced9e-ec1f-4ed0-b252-e9361c6f6f05");
    private static readonly Guid OutboxEventId =
        Guid.Parse("f4163456-c13e-40ec-8c19-fcc53e4d181e");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");
    private static readonly CompactItemEntry EquipmentItem =
        CompactItemEntry.Empty with
        {
            // This compatibility weapon is valid for every profession,
            // including SnapshotHero's profession 3.
            Id = 1_100,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 1
        };
    private static readonly CompactItemEntry OtherItem =
        CompactItemEntry.Empty with
        {
            Id = 1_101,
            Quality = 2,
            Grade = 4,
            Bound = 1,
            Stack = 1
        };
    private static readonly CompactItemEntry MountItem =
        CompactItemEntry.Empty with
        {
            Id = 14_220,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 1
        };
    private static readonly TransferSlotState UnequipBeforeState =
        new(EquipmentItem, CompactItemEntry.Empty);
    private static readonly TransferSlotState UnequipAfterState =
        new(CompactItemEntry.Empty, EquipmentItem);
    private static readonly TransferSlotState BothEmptyState =
        new(CompactItemEntry.Empty, CompactItemEntry.Empty);
    private static readonly TransferSlotState BothOccupiedState =
        new(EquipmentItem, OtherItem);
    private static readonly TransferSlotState MountBeforeState =
        new(MountItem, CompactItemEntry.Empty);
    private static readonly TransferSlotState MountAfterState =
        new(CompactItemEntry.Empty, MountItem);

    private static TransferFixture CreateFixture(
        EquipmentBagTransferExecutionResult replayResult,
        EquipmentBagTransferExecutionResult? executeResult = null,
        TransferSlotState? liveState = null,
        TransferSlotState? persistedState = null,
        bool projectionFails = false,
        bool providerUnavailable = false,
        int equipmentSlot = EquipmentSlot,
        IPetDurableCommandExecutor? petDurableCommands = null)
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var liveSnapshot = WithTransferState(
            baseSnapshot,
            liveState ?? UnequipBeforeState,
            physicalAttack: 400,
            equipmentSlot);
        var persistedSnapshot = WithTransferState(
            baseSnapshot,
            persistedState ?? UnequipAfterState,
            PersistedPhysicalAttack,
            equipmentSlot);
        var live =
            CharacterLoadSnapshotHydrator.Hydrate(liveSnapshot)?.Character
            ?? throw new InvalidOperationException(
                "Equipment transfer live fixture did not hydrate.");
        live.PositionX = 82.5f;
        live.PositionZ = -61.25f;
        live.CurrentHp = 777;
        live.CurrentMp = 333;
        live.VitalsRevision = 123;
        live.Silver = 456_789;
        live.Gold = 98_765;
        var persisted =
            CharacterLoadSnapshotHydrator
                .Hydrate(persistedSnapshot)?.Character
            ?? throw new InvalidOperationException(
                "Equipment transfer persisted fixture did not hydrate.");

        var transport = new TransferCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            baseSnapshot.AccountId,
            live);
        registry.JoinMap(
            session,
            baseSnapshot.AccountId,
            live,
            objectId: 0x0000_1448);
        var executor = providerUnavailable
            ? null
            : new TransferExecutor(
                replayResult,
                executeResult ?? replayResult,
                equipmentSlot,
                KitBagSlot);
        var snapshotReader = new TransferSnapshotReader(
            persistedSnapshot,
            projectionFails);
        var store = new TransferStore();
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            equipmentBagTransferCommands: executor,
            petDurableCommands: petDurableCommands,
            itemContent: TestItemContent.Content,
            petContent: PetContentTestCatalog.Instance);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                baseSnapshot.AccountId,
                "durable-equipment-transfer-check"));
        SetField(handler, "_character", live);
        return new TransferFixture(
            session,
            transport,
            handler,
            executor,
            store,
            registry,
            live,
            persisted);
    }

    private static CharacterAccountSnapshot WithTransferState(
        CharacterAccountSnapshot snapshot,
        TransferSlotState state,
        int physicalAttack,
        int equipmentSlot = EquipmentSlot)
    {
        var character = snapshot.Character ??
            throw new InvalidOperationException(
                "Transfer fixture requires a character.");
        var equipment = EquipmentSlots.SetSlot(
            character.Loadout.Equipment,
            character.Appearance.Profession,
            equipmentSlot,
            state.Equipment.ToCompactString());
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            KitBagSlot,
            state.KitBag.ToCompactString());
        return snapshot with
        {
            Character = character with
            {
                Loadout = character.Loadout with
                {
                    Equipment = equipment,
                    KitBag = kitBag
                },
                CalculatedStats = character.CalculatedStats with
                {
                    PhysicalAttack = physicalAttack
                }
            }
        };
    }

    private static GameCharacter CreateProjectedCharacter(
        bool transferred) =>
        CharacterLoadSnapshotHydrator.Hydrate(
            WithTransferState(
                CharacterSnapshotContractChecks.CreateValidSnapshot(),
                transferred
                    ? UnequipAfterState
                    : UnequipBeforeState,
                PersistedPhysicalAttack))!.Character;

    private static CharacterStats CopyStats(
        CharacterStats source,
        int maxHp,
        int maxMp,
        int physicalAttack,
        int? physicalDamageReduction = null,
        int? magicDamageReduction = null,
        int? criticalDamageReduction = null,
        int? lifeAbsorption = null,
        int? damageRebound = null) =>
        new()
        {
            CharacterId = source.CharacterId,
            AccountId = source.AccountId,
            Name = source.Name,
            Level = source.Level,
            MaxHp = maxHp,
            MaxMp = maxMp,
            CurrentHp = source.CurrentHp,
            CurrentMp = source.CurrentMp,
            PhysicalAttack = physicalAttack,
            PhysicalDefense = source.PhysicalDefense,
            MagicAttack = source.MagicAttack,
            MagicDefense = source.MagicDefense,
            Hit = source.Hit,
            Dodge = source.Dodge,
            Critical = source.Critical,
            CriticalResistance = source.CriticalResistance,
            DamageAbsorb = source.DamageAbsorb,
            PhysicalDamageBonus = source.PhysicalDamageBonus,
            MagicDamageBonus = source.MagicDamageBonus,
            CureBonus = source.CureBonus,
            BeCureBonus = source.BeCureBonus,
            HpRecovery = source.HpRecovery,
            MpRecovery = source.MpRecovery,
            IgnorePhysicalDefense = source.IgnorePhysicalDefense,
            IgnoreMagicDefense = source.IgnoreMagicDefense,
            PhysicalAppendDamage = source.PhysicalAppendDamage,
            MagicAppendDamage = source.MagicAppendDamage,
            CriticalDamagePercent = source.CriticalDamagePercent,
            CriticalDamageFlat = source.CriticalDamageFlat,
            PhysicalDamageReduction =
                physicalDamageReduction ?? source.PhysicalDamageReduction,
            MagicDamageReduction =
                magicDamageReduction ?? source.MagicDamageReduction,
            CriticalDamageReduction =
                criticalDamageReduction ?? source.CriticalDamageReduction,
            LifeAbsorption = lifeAbsorption ?? source.LifeAbsorption,
            DamageRebound = damageRebound ?? source.DamageRebound,
            WeaponScore = source.WeaponScore,
            WeaponRank = source.WeaponRank,
            WeaponAuraEffect = source.WeaponAuraEffect,
            ArmorScore = source.ArmorScore,
            ArmorRank = source.ArmorRank,
            ArmorAuraEffect = source.ArmorAuraEffect,
            LearnedSkillCount = source.LearnedSkillCount
        };

    private static EquipmentBagTransferExecutionReceipt
        CreateUnequipReceipt() =>
        CreateReceipt(
            EquipmentBagTransferResultStatus.Unequipped,
            UnequipBeforeState,
            UnequipBeforeState,
            OutboxEventId);

    private static EquipmentBagTransferExecutionReceipt
        CreateEquipReceipt() =>
        CreateReceipt(
            EquipmentBagTransferResultStatus.Equipped,
            UnequipAfterState,
            UnequipAfterState,
            OutboxEventId);

    private static EquipmentBagTransferExecutionReceipt
        CreateRideRuntimeBlockedReceipt() =>
        CreateReceipt(
            EquipmentBagTransferResultStatus.RideRuntimeBlocked,
            MountBeforeState,
            MountBeforeState,
            outboxEventId: null,
            equipmentSlot: EquipmentSlots.Mount);

    private static EquipmentBagTransferExecutionReceipt
        CreateMountUnequipReceipt() =>
        CreateReceipt(
            EquipmentBagTransferResultStatus.Unequipped,
            MountBeforeState,
            MountBeforeState,
            OutboxEventId,
            equipmentSlot: EquipmentSlots.Mount);

    private static EquipmentBagTransferExecutionReceipt CreateReceipt(
        EquipmentBagTransferResultStatus status,
        TransferSlotState expected,
        TransferSlotState authoritative,
        Guid? outboxEventId,
        int characterId = 19,
        int equipmentSlot = EquipmentSlot) =>
        new(
            characterId,
            equipmentSlot,
            KitBagSlot,
            status,
            expected.Equipment.ToCompactString(),
            expected.KitBag.ToCompactString(),
            authoritative.Equipment.ToCompactString(),
            authoritative.KitBag.ToCompactString(),
            outboxEventId.HasValue ? 13 : 0,
            "audit:equipment-bag-transfer:handler",
            outboxEventId);

    private static async Task InvokeTransferAsync(
        GameClientHandler handler,
        Guid? operationId,
        int packetLength = 80,
        int equipmentSlot = EquipmentSlot,
        CancellationToken cancellationToken = default)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [
                CreateTransferPacket(
                    operationId,
                    packetLength,
                    equipmentSlot),
                cancellationToken
            ]) as Task
            ?? throw new InvalidOperationException(
                "Equipment transfer handler did not return a task.");
        await invocation;
    }

    private static GamePacket CreateTransferPacket(
        Guid? operationId,
        int packetLength,
        int equipmentSlot)
    {
        if (packetLength < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(packetLength));
        }

        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.StorageItem);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)),
            0x5876_DBF0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8, sizeof(ushort)),
            checked((ushort)equipmentSlot));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10, sizeof(ushort)),
            ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort)),
            checked((ushort)(KitBagSlot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(14, sizeof(ushort)),
            checked((ushort)(KitBagSlot % 24)));
        return new GamePacket(packet, operationId);
    }

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private static object? GetFieldValue(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        return field.GetValue(handler);
    }

    private sealed record TransferFixture(
        ClientSession Session,
        TransferCaptureTransport Transport,
        GameClientHandler Handler,
        TransferExecutor? Executor,
        TransferStore Store,
        GameSessionRegistry Registry,
        GameCharacter LiveCharacter,
        GameCharacter PersistedCharacter) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private readonly record struct TransferSlotState(
        CompactItemEntry Equipment,
        CompactItemEntry KitBag);

    private sealed class TransferExecutor(
        EquipmentBagTransferExecutionResult replayResult,
        EquipmentBagTransferExecutionResult executeResult,
        int expectedEquipmentSlot,
        int expectedKitBagSlot) :
        IEquipmentBagTransferCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public EquipmentBagTransferCommand? ExecutedCommand
        { get; private set; }

        public Task<EquipmentBagTransferExecutionResult>
            TryReplayAsync(
                CommandSubject subject,
                PlayerOwnershipFence ownership,
                Guid clientOperationId,
                int equipmentSlot,
                int kitBagSlot,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(7, subject.AccountId, "transfer replay account");
            Check.Equal(
                19,
                subject.CharacterId,
                "transfer replay character");
            Check.Equal(
                OperationId,
                clientOperationId,
                "transfer replay UUID");
            Check.Equal(
                expectedEquipmentSlot,
                equipmentSlot,
                "transfer replay equipment slot");
            Check.Equal(
                expectedKitBagSlot,
                kitBagSlot,
                "transfer replay bag slot");
            return Task.FromResult(replayResult);
        }

        public Task<EquipmentBagTransferExecutionResult> ExecuteAsync(
            CommandEnvelope<EquipmentBagTransferCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(executeResult);
        }
    }

    private sealed class TransferSnapshotReader(
        CharacterAccountSnapshot snapshot,
        bool fails) : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fails)
            {
                throw new IOException(
                    "Injected equipment transfer projection failure.");
            }
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "transfer projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class TransferStore : GameStoreTestStub
    {
        public int UnequipCount { get; private set; }
        public int EquipCount { get; private set; }
        public GameCharacter? UnequipResult { get; set; }
        public GameCharacter? EquipResult { get; set; }

        public override Task<GameCharacter?>
            MoveEquipmentToKitBagAsync(
                int accountId,
                int characterId,
                int equipmentSlot,
                int kitBagSlot,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnequipCount++;
            return Task.FromResult(UnequipResult);
        }

        public override Task<GameCharacter?>
            MoveKitBagToEquipmentAsync(
                int accountId,
                int characterId,
                int kitBagSlot,
                int requestedEquipmentSlot,
                CancellationToken cancellationToken = default,
                bool requireEmptyEquipmentSlot = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EquipCount++;
            return Task.FromResult(EquipResult);
        }

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                (UnequipResult ?? EquipResult)?.CalculatedStats);
        }
    }
}
