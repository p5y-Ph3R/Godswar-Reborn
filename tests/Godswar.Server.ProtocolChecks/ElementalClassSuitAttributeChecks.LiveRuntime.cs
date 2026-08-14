using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static readonly MethodInfo ElementalHandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly MethodInfo ElementalStatusPacketMethod =
        typeof(GameClientHandler).GetMethod(
            "BuildLocalPlayerStatusUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.BuildLocalPlayerStatusUpdate was not found.");

    private static readonly MethodInfo ElementalPassiveStatsMethod =
        typeof(GameClientHandler).GetMethod(
            "ApplyElementalPassiveStats",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.ApplyElementalPassiveStats was not found.");

    private static async Task CheckElementalLiveHandlersAsync()
    {
        await CheckElementalLiveMovementAsync();
        await CheckElementalLiveRecoveryAsync();
        await CheckElementalLivePeriodicDamageAsync();
        await CheckElementalPriestWitherAsync();
        await CheckPveElementalReachAsync();
        await CheckElementalLivePvpAsync();
        CheckEarthPassiveRefresh();
        CheckRevisionEventIdentity();
    }

    private static async Task CheckElementalLiveMovementAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var store = new ElementalPositionStore();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            1);
        var character = ElementalLiveCharacter(
            1_401,
            41,
            ownership);
        SetElementalProfile(
            character,
            LiveProfile((ElementKind.Wind, 6, default)));
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership);
        var handler = CreateElementalLiveHandler(
            socket.Session,
            store,
            registry,
            character);
        var fence = new ElementalCombatSessionFence(
            character.Id,
            character.CurrentMap,
            ownership);
        var statusAt = DateTimeOffset.UtcNow;
        ApplyLiveStatus(
            registry,
            socket.Session,
            fence,
            ElementalEffectKind.Shock,
            potencyBasisPoints: 1_000,
            statusAt,
            eventId: 41_001);

        await InvokeElementalPacketAsync(
            handler,
            ElementalWalkPacket(3f, 4f));
        Check.True(
            character.PositionX == 0f &&
            character.PositionZ == 0f &&
            character.PositionRevision == 0 &&
            store.SaveAttempts == 0,
            "live legacy walk rejects Shock before position, AOI, or persistence commit");

        registry.AdvancePlayerLifeRevision(socket.Session);
        await InvokeElementalPacketAsync(
            handler,
            ElementalWalkPacket(3f, 4f));
        Check.True(
            character.PositionX == 3f &&
            character.PositionZ == 4f &&
            character.PositionRevision == 1 &&
            store.SaveAttempts == 1,
            "accepted live walk commits one authoritative position revision");

        var direct = new DeterministicCombatEventContext(
            41_002,
            character.CurrentMap,
            character.Id,
            9_999,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectBasicAttack,
            Committed: true,
            IsPvp: false,
            default);
        Check.True(
            registry.TryAdjustElementalOutgoingHit(
                socket.Session,
                fence,
                direct,
                character.ElementalEquipment,
                originalDamage: 1_000,
                targetCurrentHealth: 10_000,
                targetMaximumHealth: 10_000,
                out var momentum) &&
            momentum.AeolusMomentumPendingCommit &&
            momentum.AdjustedDamage == 1_100,
            "live accepted five-unit walk advances Wind Momentum from position commit");

        statusAt = DateTimeOffset.UtcNow;
        ApplyLiveStatus(
            registry,
            socket.Session,
            fence,
            ElementalEffectKind.Drench,
            1_000,
            statusAt,
            41_003);
        ApplyLiveStatus(
            registry,
            socket.Session,
            fence,
            ElementalEffectKind.Gale,
            1_000,
            statusAt,
            41_004);
        var statusPacket = (byte[]?)ElementalStatusPacketMethod.Invoke(
            handler,
            null) ?? throw new InvalidOperationException(
                "Elemental status projection returned no packet.");
        var movementMultiplier = BinaryPrimitives
            .ReadSingleLittleEndian(statusPacket.AsSpan(56, 4));
        Check.True(
            MathF.Abs(movementMultiplier - 1.0395f) < 0.0001f,
            "live status projection composes Wind +5%, Drench -10%, then Gale +10%");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static async Task CheckElementalLiveRecoveryAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            1);
        var character = ElementalLiveCharacter(
            1_402,
            42,
            ownership);
        character.CurrentHp = 8_000;
        character.MaxHp = 10_000;
        character.CurrentMp = 800;
        character.MaxMp = 1_000;
        SetElementalProfile(
            character,
            LiveProfile(
                (ElementKind.Water, 3, default),
                (ElementKind.Light, 3, default)));
        var joinedAt = new DateTimeOffset(
            2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            joinedAt);
        var fence = new ElementalCombatSessionFence(
            character.Id,
            character.CurrentMap,
            ownership);
        ApplyLiveStatus(
            registry,
            socket.Session,
            fence,
            ElementalEffectKind.Wither,
            1_000,
            joinedAt,
            42_001);

        await registry.AdvancePlayerRecoveryOnceAsync(
            joinedAt.AddSeconds(6),
            CancellationToken.None);
        Check.True(
            character.CurrentHp == 8_161 &&
            character.CurrentMp == 852 &&
            character.VitalsRevision == 1 &&
            registry.GetElementalRecoveryRevisionForDiagnostics(
                socket.Session) == 1,
            "live recovery commits base plus Water, Light amplification, then Dark Wither exactly once");

        await registry.AdvancePlayerRecoveryOnceAsync(
            joinedAt.AddSeconds(6),
            CancellationToken.None);
        Check.True(
            character.CurrentHp == 8_161 &&
            character.CurrentMp == 852 &&
            character.VitalsRevision == 1 &&
            registry.GetElementalRecoveryRevisionForDiagnostics(
                socket.Session) == 1,
            "polling the same recovery deadline cannot replay elemental recovery");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static void CheckEarthPassiveRefresh()
    {
        var character = ElementalLiveCharacter(
            1_403,
            43,
            new PlayerOwnershipFence(Guid.NewGuid(), 1));
        var baseStats = new CharacterStats
        {
            MaxHp = 1_000,
            MaxMp = 500,
            CurrentHp = 1_000,
            CurrentMp = 500
        };
        baseStats.ApplyTo(character);
        SetElementalProfile(
            character,
            LiveProfile((ElementKind.Earth, 3, default)));
        ElementalPassiveStatsMethod.Invoke(
            null,
            [character, baseStats]);
        ElementalPassiveStatsMethod.Invoke(
            null,
            [character, baseStats]);
        Check.Equal(
            1_080,
            character.MaxHp,
            "Earth max-HP refresh derives from base stats and cannot compound");

        character.CurrentHp = 1_080;
        SetElementalProfile(character, LiveProfile());
        ElementalPassiveStatsMethod.Invoke(
            null,
            [character, baseStats]);
        Check.True(
            character.MaxHp == 1_000 &&
            character.CurrentHp == 1_000,
            "removing Earth pieces restores base max HP and clamps current HP");
    }

    private static void CheckRevisionEventIdentity()
    {
        var at = new DateTimeOffset(
            2026, 8, 14, 0, 0, 6, TimeSpan.Zero);
        var first = AuthoredElementalCombatV1.AcceptedMovementEvent(
            1_404,
            7,
            acceptedPositionRevision: 9,
            at);
        var replay = AuthoredElementalCombatV1.AcceptedMovementEvent(
            1_404,
            7,
            acceptedPositionRevision: 9,
            at.AddSeconds(1));
        var recovery = AuthoredElementalCombatV1.RecoveryEvent(
            1_404,
            7,
            acceptedRecoveryRevision: 9,
            at);
        Check.True(
            first.EventId == replay.EventId &&
            first.EventId != recovery.EventId &&
            first.EventId != 0 &&
            ElementalEffectExecutionPolicy.DeterministicRollBasisPoints(
                first,
                ElementKind.Wind) ==
            ElementalEffectExecutionPolicy.DeterministicRollBasisPoints(
                replay,
                ElementKind.Wind),
            "event identity and RNG depend on server-owned revision/provenance, not replay timing");
    }

    private static GameCharacter ElementalLiveCharacter(
        int characterId,
        int accountId,
        PlayerOwnershipFence ownership) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = $"ElementalLive{characterId}",
            CurrentMap = 0,
            Level = 1,
            Profession = 0,
            PositionX = 0,
            PositionZ = 0,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 1_000,
            MaxMp = 1_000,
            CheckpointOwnerId = ownership.OwnerId,
            CheckpointOwnerGeneration = ownership.Generation
        };

    private static ElementalEquipmentProfile LiveProfile(
        params (ElementKind Element, int Pieces, ElementalEffectTotals Totals)[]
            values)
    {
        var totals = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => 0);
        foreach (var value in values)
        {
            totals[value.Element] = value.Totals;
            counts[value.Element] = value.Pieces;
        }

        var active = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            value => ElementalResonanceCatalog.ActiveFor(
                value,
                counts[value]));
        return new(totals, counts, active);
    }

    private static void SetElementalProfile(
        GameCharacter character,
        ElementalEquipmentProfile profile)
    {
        var property = typeof(GameCharacter).GetProperty(
            nameof(GameCharacter.ElementalEquipment),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "GameCharacter.ElementalEquipment was not found.");
        property.SetValue(character, profile);
    }

    private static void BindElementalLiveSession(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        PlayerOwnershipFence ownership,
        DateTimeOffset? joinedAt = null)
    {
        registry.ReplaceAccountSession(character.AccountId, session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                character.AccountId,
                session,
                ownership),
            "live elemental fixture binds ownership");
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: joinedAt);
    }

    private static void ApplyLiveStatus(
        GameSessionRegistry registry,
        ClientSession session,
        ElementalCombatSessionFence fence,
        ElementalEffectKind effect,
        int potencyBasisPoints,
        DateTimeOffset appliedAt,
        ulong eventId)
    {
        var sourceId = fence.CharacterId + 10_000;
        var combatEvent = new DeterministicCombatEventContext(
            eventId,
            fence.MapId,
            sourceId,
            fence.CharacterId,
            appliedAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectSkill,
            Committed: true,
            IsPvp: false,
            default);
        var application = new ElementalEffectApplication(
            ElementFor(effect),
            effect,
            sourceId,
            fence.CharacterId,
            eventId,
            appliedAt.ToUnixTimeMilliseconds(),
            appliedAt.AddMinutes(1).ToUnixTimeMilliseconds(),
            potencyBasisPoints,
            ApplicationChanceBasisPoints: 10_000,
            TargetResistanceBasisPoints: 0,
            PeriodicDamageTotal: 0,
            PeriodicTickCount: 0,
            CombatEventProvenance.ElementalStatus);
        Check.True(
            registry.TryApplyElementalApplication(
                session,
                fence,
                combatEvent,
                application),
            $"live fixture applies {effect}");
    }

    private static ElementKind ElementFor(ElementalEffectKind effect) =>
        ElementalAttributeCatalog.Effects.Single(
            value => value.Effect == effect).Element;

    private static GameClientHandler CreateElementalLiveHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        GameCharacter character)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        SetElementalHandlerField(
            handler,
            "_account",
            new AccountIdentity(
                character.AccountId,
                $"elemental-{character.AccountId}"));
        SetElementalHandlerField(handler, "_character", character);
        SetElementalHandlerField(handler, "_registered", true);
        SetElementalHandlerField(
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

    private static void SetElementalHandlerField<T>(
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

    private static async Task InvokeElementalPacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = ElementalHandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    private static GamePacket ElementalWalkPacket(float x, float z)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.Walk);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8, 4),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12, 4),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16, 4),
            1f);
        return new(packet);
    }

    private sealed class ElementalPositionStore : GameStoreTestStub
    {
        public int SaveAttempts { get; private set; }

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return Task.CompletedTask;
        }
    }
}
