using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static async Task CheckElementalLivePeriodicDamageAsync()
    {
        await CheckPlayerBurnClockAsync();
        await CheckMonsterBurnClockAsync();
        await CheckMonsterBurnStaleLifeFenceAsync();
    }

    private static async Task CheckPlayerBurnClockAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(
            Guid.Parse("abababab-abab-abab-abab-abababababab"),
            1);
        var character = ElementalLiveCharacter(1_405, 45, ownership);
        character.CurrentHp = 350;
        character.MaxHp = 1_000;
        var appliedAt = new DateTimeOffset(
            2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            appliedAt);
        ApplyLiveBurn(
            registry,
            socket.Session,
            new(character.Id, character.CurrentMap, ownership),
            sourceCharacterId: 9_405,
            eventId: 45_001,
            totalDamage: 400,
            appliedAt);

        await registry.AdvancePlayerRecoveryOnceAsync(
            appliedAt.AddMilliseconds(999),
            CancellationToken.None);
        Check.True(
            character.CurrentHp == 350 &&
            character.VitalsRevision == 0,
            "Burn does not run before its authored one-second tick deadline");

        await registry.AdvancePlayerRecoveryOnceAsync(
            appliedAt.AddSeconds(1),
            CancellationToken.None);
        Check.True(
            character.CurrentHp == 250 &&
            character.VitalsRevision == 1 &&
            registry.GetPlayerLifeRevision(socket.Session) == 0,
            "authoritative periodic clock commits the first player Burn tick once");

        await registry.AdvancePlayerRecoveryOnceAsync(
            appliedAt.AddSeconds(4),
            CancellationToken.None);
        Check.True(
            character.CurrentHp == 0 &&
            character.VitalsRevision == 2 &&
            registry.GetPlayerLifeRevision(socket.Session) == 1,
            "bounded overdue player Burn ticks commit lethal damage and advance life authority");

        await registry.AdvancePlayerRecoveryOnceAsync(
            appliedAt.AddSeconds(4),
            CancellationToken.None);
        Check.True(
            character.CurrentHp == 0 && character.VitalsRevision == 2,
            "the same periodic deadline cannot replay committed player Burn damage");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static async Task CheckMonsterBurnClockAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(
            Guid.Parse("acacacac-acac-acac-acac-acacacacacac"),
            1);
        var character = ElementalLiveCharacter(1_406, 46, ownership);
        SetElementalProfile(
            character,
            LiveProfile((
                ElementKind.Fire,
                1,
                new ElementalEffectTotals(
                    EffectPotencyBasisPoints: 1_000,
                    EffectResistanceBasisPoints: 0,
                    ApplicationChanceBasisPoints: 10_000))));
        var committedAt = new DateTimeOffset(
            2026, 8, 14, 0, 1, 0, TimeSpan.Zero);
        const uint monsterObjectId = 9_406;
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [ElementalBurnMonster(monsterObjectId, 1_000)],
            committedAt);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            committedAt);
        var viewerOwnership = new PlayerOwnershipFence(
            Guid.Parse("aeaeaeae-aeae-aeae-aeae-aeaeaeaeaeae"),
            1);
        var viewer = ElementalLiveCharacter(
            1_408,
            48,
            viewerOwnership);
        BindElementalLiveSession(
            registry,
            viewerSocket.Session,
            viewer,
            viewerOwnership,
            committedAt);
        await using (var visibility =
                     await registry.BeginMonsterVisibilityTransitionAsync(
                         viewerSocket.Session,
                         viewer.CurrentMap,
                         viewer.PositionX,
                         viewer.PositionZ,
                         CancellationToken.None)
                     ?? throw new InvalidOperationException(
                         "monster Burn viewer transition was unavailable"))
        {
            visibility.Commit();
        }
        Check.True(
            registry.TryGetMonsterSnapshot(
                socket.Session,
                character.CurrentMap,
                monsterObjectId,
                out var before),
            "monster Burn fixture resolves the authoritative target");
        Check.True(
            registry.TryApplyMonsterDamageGuarded(
                character.CurrentMap,
                monsterObjectId,
                damage: 100,
                character.Id,
                before.SpawnGeneration,
                before.HealthRevision,
                committedAt,
                out var direct),
            "monster Burn fixture commits one guarded direct hit");
        var eventId = FindFireApplicationEventId(
            character,
            monsterObjectId,
            committedAt);
        var applied = await registry.CommitAndPublishPveElementalHitsAsync(
            socket.Session,
            character,
            CombatEventProvenance.DirectBasicAttack,
            [new PveElementalCommittedHit(eventId, 0, direct)],
            committedAt,
            CancellationToken.None);
        Check.True(
            applied.Applications.Count == 1 &&
            applied.Applications[0].Effect == ElementalEffectKind.Burn,
            "committed PvE hit registers one server-owned monster Burn");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);

        await registry.AdvancePlayerRecoveryOnceAsync(
            committedAt.AddSeconds(4),
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var after) &&
            after.CurrentHealth == 890 &&
            after.HealthRevision == direct.Monster.HealthRevision + 1,
            "four overdue monster Burn ticks coalesce into one guarded health revision after source disconnect");
        var reconcileRemove = await viewerSocket.ReadPacketAsync(12);
        var reconcileSpawn = await viewerSocket.ReadPacketAsync(108);
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(
                reconcileRemove.AsSpan(8, 4)) == monsterObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                reconcileSpawn.AsSpan(8, 4)) == monsterObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                reconcileSpawn.AsSpan(20, 4)) == 890 &&
            viewerSocket.Available == 0,
            "offline-source monster Burn publishes health reconciliation without a phantom attacker");

        await registry.AdvancePlayerRecoveryOnceAsync(
            committedAt.AddSeconds(4),
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var replay) &&
            replay.CurrentHealth == after.CurrentHealth &&
            replay.HealthRevision == after.HealthRevision,
            "monster Burn replay at the same deadline is inert");

        registry.Remove(viewerSocket.Session);
        registry.RemoveAccountSession(viewer.AccountId, viewerSocket.Session);
    }

    private static async Task CheckMonsterBurnStaleLifeFenceAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var character = ElementalLiveCharacter(1_413, 53, ownership);
        SetElementalProfile(
            character,
            LiveProfile((
                ElementKind.Fire,
                1,
                new ElementalEffectTotals(1_000, 0, 2_000))));
        var at = DateTimeOffset.UtcNow;
        const uint objectId = 9_415;
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [ElementalBurnMonster(objectId, 1_000)],
            at);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            at);
        var found = registry.TryGetMonsterSnapshot(
            socket.Session,
            character.CurrentMap,
            objectId,
            out var before);
        Check.True(found,
            "stale-life Burn fixture resolves its target");
        var initialDamageApplied = registry.TryApplyMonsterDamageGuarded(
            character.CurrentMap,
            objectId,
            damage: 100,
            character.Id,
            before.SpawnGeneration,
            before.HealthRevision,
            at,
            out var direct);
        Check.True(
            initialDamageApplied,
            "stale-life Burn fixture commits its initial direct hit");
        var applied = await registry.CommitAndPublishPveElementalHitsAsync(
            socket.Session,
            character,
            CombatEventProvenance.DirectBasicAttack,
            [new(
                FindFireApplicationEventId(character, objectId, at),
                0,
                direct)],
            at,
            CancellationToken.None);
        Check.True(
            applied.Applications is
            [{ Effect: ElementalEffectKind.Burn }],
            "stale-life fixture registers monster Burn before death");
        Check.True(
            registry.TryApplyMonsterDamageGuarded(
                character.CurrentMap,
                objectId,
                damage: 1_000,
                character.Id,
                direct.Monster.SpawnGeneration,
                direct.Monster.HealthRevision,
                at.AddMilliseconds(1),
                out var killed) &&
            killed.Killed,
            "an independent guarded hit kills the Burn target before its first tick");

        await registry.AdvancePlayerRecoveryOnceAsync(
            at.AddSeconds(4),
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var corpse) &&
            corpse.CurrentHealth == 0 &&
            corpse.HealthRevision == killed.Monster.HealthRevision,
            "periodic polling clears stale dead-target Burn without another health mutation");

        await registry.AdvanceMonsterWorldOnceAsync(
            at.AddSeconds(6),
            CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(
            at.AddSeconds(6),
            CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(
            at.AddSeconds(11),
            CancellationToken.None);
        await registry.AdvancePlayerRecoveryOnceAsync(
            at.AddSeconds(12),
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var respawned) &&
            respawned.IsAlive &&
            respawned.IsSpawned &&
            respawned.SpawnGeneration ==
                killed.Monster.SpawnGeneration + 1 &&
            respawned.CurrentHealth == 1_000 &&
            respawned.HealthRevision == 0,
            "stale Burn identity cannot cross a monster life/spawn revision boundary");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static async Task CheckElementalPriestWitherAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(store: null);
        var ownership = new PlayerOwnershipFence(
            Guid.Parse("adadadad-adad-adad-adad-adadadadadad"),
            1);
        var character = ElementalLiveCharacter(1_407, 47, ownership);
        character.CurrentHp = 5_000;
        var now = DateTimeOffset.UtcNow;
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            now);
        var store = new ElementalPositionStore();
        var handler = CreateElementalLiveHandler(
            socket.Session,
            store,
            registry,
            character);
        ApplyLiveStatus(
            registry,
            socket.Session,
            new(character.Id, character.CurrentMap, ownership),
            ElementalEffectKind.Wither,
            potencyBasisPoints: 1_000,
            now,
            eventId: 47_001);

        var targetType = typeof(GameClientHandler).GetNestedType(
            "PriestHealTarget",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "PriestHealTarget was not found.");
        var target = Activator.CreateInstance(
            targetType,
            [
                socket.Session,
                character.AccountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                true,
                null
            ]) ?? throw new InvalidOperationException(
                "PriestHealTarget could not be created.");
        var method = typeof(GameClientHandler).GetMethod(
            "ApplyPriestHeal",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ApplyPriestHeal was not found.");
        _ = method.Invoke(handler, [target, 1_000, 0, now]);
        Check.Equal(
            5_900,
            character.CurrentHp,
            "direct Priest healing applies target-owned Wither before HP mutation");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static void ApplyLiveBurn(
        GameSessionRegistry registry,
        Godswar.Server.Networking.ClientSession targetSession,
        ElementalCombatSessionFence targetFence,
        int sourceCharacterId,
        ulong eventId,
        long totalDamage,
        DateTimeOffset appliedAt)
    {
        var combatEvent = new DeterministicCombatEventContext(
            eventId,
            targetFence.MapId,
            sourceCharacterId,
            targetFence.CharacterId,
            appliedAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectSkill,
            Committed: true,
            IsPvp: false,
            default);
        var application = new ElementalEffectApplication(
            ElementKind.Fire,
            ElementalEffectKind.Burn,
            sourceCharacterId,
            targetFence.CharacterId,
            eventId,
            appliedAt.ToUnixTimeMilliseconds(),
            appliedAt.AddSeconds(4).ToUnixTimeMilliseconds(),
            EffectivePotencyBasisPoints: 1_000,
            ApplicationChanceBasisPoints: 10_000,
            TargetResistanceBasisPoints: 0,
            totalDamage,
            PeriodicTickCount: 4,
            CombatEventProvenance.ElementalStatus);
        Check.True(
            registry.TryApplyElementalApplication(
                targetSession,
                targetFence,
                combatEvent,
                application),
            "player Burn fixture applies one target-owned status");
    }

    private static ulong FindFireApplicationEventId(
        GameCharacter character,
        uint monsterObjectId,
        DateTimeOffset committedAt)
    {
        for (ulong eventId = 46_001; eventId < 56_001; eventId++)
        {
            var combatEvent = new DeterministicCombatEventContext(
                eventId,
                character.CurrentMap,
                character.Id,
                monsterObjectId,
                committedAt.ToUnixTimeMilliseconds(),
                CombatEventProvenance.DirectBasicAttack,
                Committed: true,
                IsPvp: false,
                default);
            if (ElementalEffectExecutionPolicy.DeterministicRollBasisPoints(
                    combatEvent,
                    ElementKind.Fire) < 2_000)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            "No deterministic Fire application event was found.");
    }

    private static CapturedMonsterSpawn ElementalBurnMonster(
        uint objectId,
        uint maximumHealth)
    {
        const string template = "ElementalBurnMonster";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            maximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(template).CopyTo(packet.AsSpan(44));
        return new(
            MapId: 0,
            SceneKey: "Sparta",
            template,
            template,
            objectId,
            X: 0f,
            Z: 0f,
            packet);
    }
}
