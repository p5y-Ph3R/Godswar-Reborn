using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private const uint MedusaHandlerLocalObjectId = 0x0000_1448;
    private const uint MedusaHandlerSkillId = 530;
    private const int MedusaHandlerBagSlot = 25;

    private static readonly MethodInfo MedusaHandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly MethodInfo MedusaStopCastsMethod =
        typeof(GameClientHandler).GetMethod(
            "StopPendingSkillCastsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.StopPendingSkillCastsAsync was not found.");

    private static readonly MethodInfo MedusaRegisterCastInterruptionMethod =
        typeof(GameClientHandler).GetMethod(
            "RegisterSkillCastInterruption",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.RegisterSkillCastInterruption was not found.");

    private static readonly MethodInfo MedusaUnregisterCastInterruptionMethod =
        typeof(GameClientHandler).GetMethod(
            "UnregisterSkillCastInterruption",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.UnregisterSkillCastInterruption was not found.");

    private static readonly MethodInfo MedusaRealtimeTickMethod =
        typeof(GameClientHandler).GetMethod(
            "ProcessRealtimeMovementTickAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.ProcessRealtimeMovementTickAsync was not found.");

    private static readonly MethodInfo MedusaRealtimePublishMethod =
        typeof(GameClientHandler).GetMethod(
            "PublishRealtimeMovementEffectsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.PublishRealtimeMovementEffectsAsync was not found.");

    private static readonly PropertyInfo MedusaHasPendingCastProperty =
        typeof(GameClientHandler).GetProperty(
            "HasPendingSkillCast",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HasPendingSkillCast was not found.");

    private static readonly FieldInfo MedusaBasicCooldownField =
        typeof(GameClientHandler).GetField(
            "_nextBasicAttackAt",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler._nextBasicAttackAt was not found.");

    private static GameClientHandler CreateMedusaHandler(
        ClientSession session,
        GameSessionRegistry registry,
        GameCharacter character,
        MedusaHandlerStore store,
        CountingTalentUpgradeExecutor? talents = null)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            talentUpgradeCommands: talents,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime,
            itemContent: TestItemContent.Content,
            petContent: PetContentTestCatalog.Instance);
        SetMedusaHandlerField(
            handler,
            "_account",
            new AccountIdentity(
                character.AccountId,
                $"medusa-status-{character.AccountId}"));
        SetMedusaHandlerField(handler, "_character", character);
        SetMedusaHandlerField(handler, "_registered", true);
        SetMedusaHandlerField(
            handler,
            "_worldPresenceAnnounced",
            true);
        SetMedusaHandlerField(handler, "_clientReadyReceived", true);
        SetMedusaHandlerField(handler, "_playerDetailSent", true);
        SetMedusaHandlerField(handler, "_enterUiReadyReceived", true);
        SetMedusaHandlerField(
            handler,
            "_npcVisibility",
            new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
                [],
                static npc => npc.ObjectId,
                static npc => npc.X,
                static npc => npc.Z,
                "NPC"));
        return handler;
    }

    private static void SetMedusaHandlerField<T>(
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

    private static async Task InvokeMedusaPacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = MedusaHandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    private static async Task<object> InvokeMedusaRealtimeTickAsync(
        GameClientHandler handler)
    {
        var task = MedusaRealtimeTickMethod.Invoke(
            handler,
            [CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Realtime movement tick returned no task.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException(
                "Realtime movement tick returned no effects.");
    }

    private static byte[]? MedusaRealtimeEffect(
        object effects,
        string propertyName) =>
        effects.GetType().GetProperty(propertyName)?.GetValue(effects)
            as byte[];

    private static object? MedusaRealtimeEffectValue(
        object effects,
        string propertyName) =>
        effects.GetType().GetProperty(propertyName)?.GetValue(effects);

    private static async Task PublishMedusaRealtimeEffectsAsync(
        GameClientHandler handler,
        object effects)
    {
        var task = MedusaRealtimePublishMethod.Invoke(
            handler,
            [effects, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Realtime movement publisher returned no task.");
        await task;
    }

    private static bool MedusaHasPendingCast(
        GameClientHandler handler) =>
        (bool)(MedusaHasPendingCastProperty.GetValue(handler)
            ?? throw new InvalidOperationException(
                "HasPendingSkillCast returned no value."));

    private static DateTimeOffset MedusaBasicCooldown(
        GameClientHandler handler) =>
        (DateTimeOffset)(MedusaBasicCooldownField.GetValue(handler)
            ?? throw new InvalidOperationException(
                "Basic-attack cooldown returned no value."));

    private static async Task StopMedusaPendingCastsAsync(
        GameClientHandler handler)
    {
        var task = MedusaStopCastsMethod.Invoke(handler, null) as Task
            ?? throw new InvalidOperationException(
                "StopPendingSkillCastsAsync returned no task.");
        await task;
    }

    private static void RegisterMedusaCastInterruption(
        GameClientHandler handler) =>
        MedusaRegisterCastInterruptionMethod.Invoke(handler, null);

    private static void UnregisterMedusaCastInterruption(
        GameClientHandler handler) =>
        MedusaUnregisterCastInterruptionMethod.Invoke(handler, null);

    private static GamePacket MedusaControlPacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        return new GamePacket(packet);
    }

    private static GamePacket MedusaWalkPacket(
        float x,
        float z,
        uint state = 0xCAFE_1448)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), state);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16), 1f);
        return new GamePacket(packet);
    }

    private static GamePacket MedusaBasicAttackPacket(
        GameCharacter character,
        uint targetObjectId)
    {
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.BasicAttack);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            MedusaHandlerLocalObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            character.PositionZ);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            targetObjectId);
        return new GamePacket(packet);
    }

    private static GamePacket MedusaSkillPacket(
        GameCharacter character,
        MonsterRuntimeSnapshot target)
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 40);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            MedusaHandlerLocalObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            MedusaHandlerSkillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            target.ObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(24),
            character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            character.PositionZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32),
            target.X);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36),
            target.Z);
        return new GamePacket(packet);
    }

    private static GamePacket MedusaTalentPacket() => new(
        Convert.FromHexString(
            "1C004127481400000000000000000000000000000A00000000000000"));

    private static GamePacket MedusaEquipmentPacket()
    {
        var packet = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.BreakItem);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            MedusaHandlerLocalObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12),
            checked((ushort)(MedusaHandlerBagSlot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(14),
            checked((ushort)(MedusaHandlerBagSlot % 24)));
        return new GamePacket(packet);
    }

    private static void InstallMedusaHandlerEquipment(
        GameCharacter character)
    {
        if (string.IsNullOrEmpty(character.Equipment))
        {
            character.Equipment =
                GameDefaults.DefaultEquipment(character.Profession);
        }
        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            MedusaHandlerBagSlot,
            (CompactItemEntry.Empty with
            {
                Id = 1_100,
                Quality = 3,
                Grade = 5,
                Bound = 1,
                Stack = 1
            }).ToCompactString());
    }

    private static async Task PrepareMedusaMonsterVisibilityAsync(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character)
    {
        await using var transition =
            await registry.BeginMonsterVisibilityTransitionAsync(
                session,
                character.CurrentMap,
                character.PositionX,
                character.PositionZ,
                CancellationToken.None)
            ?? throw new InvalidOperationException(
                "Medusa handler visibility was unavailable.");
        transition.Commit();
    }

    private sealed class MedusaHandlerStore : GameStoreTestStub
    {
        public MedusaHandlerStore(GameCharacter character)
        {
            Character = character;
        }

        public GameCharacter Character { get; }
        public int PositionWrites { get; private set; }
        public int VitalsWrites { get; private set; }
        public int SkillReads { get; private set; }
        public int EquipmentActivations { get; private set; }

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            PositionWrites++;
            return Task.CompletedTask;
        }

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            VitalsWrites++;
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            SkillReads++;
            return Task.FromResult<IReadOnlyList<SkillState>>(
                [new SkillState
                {
                    SkillId = checked((int)MedusaHandlerSkillId),
                    Level = 1
                }]);
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
            EquipmentActivations++;
            return Task.FromResult<GameCharacter?>(null);
        }
    }

    private sealed class CountingTalentUpgradeExecutor :
        ITalentUpgradeCommandExecutor
    {
        public int Executions { get; private set; }

        public Task<TalentUpgradeExecutionResult> ExecuteAsync(
            CommandEnvelope<TalentUpgradeCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            Executions++;
            return Task.FromResult(
                new TalentUpgradeExecutionResult(
                    TalentUpgradeExecutionDisposition
                        .PreconditionFailed));
        }
    }
}
