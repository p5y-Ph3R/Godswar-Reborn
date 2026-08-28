using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckPlayerMonsterDamageTransactionAsync()
    {
        CheckMapTwoHundredDoesNotInferMedusaOwnership();
        CheckBoundRawDamageBypassesAreClosed();
        CheckBossTypedDamageChannels();
        CheckOwnerAmplifierOrdering();
        CheckDamageIdentityAndClockPreflight();
        CheckLethalDamageClaimsExactlyOnce();
        CheckDamageDoesNotConsumePeriodicEvents();
        await CheckCurrentMembershipCommitRaceAsync();
        await CheckMembershipEpochRejectsRejoinedDamageAsync();
        await CheckRegistryMembershipCommitRaceAsync();
        await CheckSameMapTransferAuthorityAsync();
        await CheckBoundMedusaElementalSuppressionAsync();
    }

    private static void CheckMapTwoHundredDoesNotInferMedusaOwnership()
    {
        var fixture = CreateAttachmentFixture();
        _ = fixture.Map.InitializeMonsters(
            fixture.Inputs.Definitions,
            StartedAt,
            respawnPolicy: MonsterRespawnPolicy.Timed);
        var medusaTemplate = fixture.Inputs.RunSpawns
            .Single(spawn => spawn.RosterSpawnId == "Medusa")
            .TemplateKey;
        var medusa = fixture.Map.SnapshotMonsters().Single(monster =>
            string.Equals(
                monster.Definition.TemplateKey,
                medusaTemplate,
                StringComparison.Ordinal));
        var commit = CommitTypedDamage(
            fixture.Map,
            medusa,
            attackerCharacterId: 101,
            CombatDamageChannel.Physical,
            damage: 1_000,
            StartedAt.AddSeconds(1));

        Check.True(
            commit.Outcome ==
                MedusaPlayerMonsterDamageOutcome.AppliedUnbound &&
            commit.Resolution.Damage == 1_000 &&
            commit.DamageResult is { } applied &&
            applied.BeforeHealth - applied.AfterHealth == 1_000 &&
            commit.Defeat is null &&
            !fixture.Map.TryGetMedusaOwnershipSnapshot(out _),
            "map 200 and a Medusa-shaped template remain ordinary without explicit attached ownership");
    }

    private static void CheckBoundRawDamageBypassesAreClosed()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "raw-bypass fixture attaches");
        var target = FindMonster(fixture.Map, "Medusa");
        var before = target.CurrentHealth;

        Check.True(
            !fixture.Map.TryApplyMonsterDamage(
                target.ObjectId,
                777,
                attackerCharacterId: 101,
                target.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out _) &&
            !fixture.Map.TryApplyMonsterDamageGuarded(
                target.ObjectId,
                777,
                attackerCharacterId: 101,
                target.SpawnGeneration,
                target.HealthRevision,
                StartedAt.AddSeconds(1),
                out _) &&
            !fixture.Map.TryApplyMonsterPeriodicDamageGuarded(
                target.ObjectId,
                777,
                sourceCharacterId: 101,
                target.SpawnGeneration,
                target.HealthRevision,
                StartedAt.AddSeconds(1),
                out _) &&
            fixture.Map.TryGetMonsterSnapshot(
                target.ObjectId,
                out var unchanged) &&
            unchanged.CurrentHealth == before &&
            unchanged.HealthRevision == target.HealthRevision &&
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
            ownership.Run.TeamScore == 0,
            "raw, guarded/rebound, and channelless periodic seams cannot bypass an attached Medusa transaction");
    }

    private static void CheckBossTypedDamageChannels()
    {
        CheckBossDamage(
            "Stheno",
            CombatDamageChannel.Physical,
            sourceDamage: 1_000,
            expectedDamage: 1_000,
            "Stheno accepts full physical damage");
        CheckBossDamage(
            "Stheno",
            CombatDamageChannel.Magic,
            sourceDamage: 1_000,
            expectedDamage: 100,
            "Stheno reduces magical damage by 90 percent");
        CheckBossDamage(
            "Medusa",
            CombatDamageChannel.Magic,
            sourceDamage: 1_000,
            expectedDamage: 1_000,
            "Medusa accepts full magical damage");
        CheckBossDamage(
            "Medusa",
            CombatDamageChannel.Physical,
            sourceDamage: 1_000,
            expectedDamage: 100,
            "Medusa reduces physical damage by 90 percent");
        CheckBossDamage(
            "Medusa",
            CombatDamageChannel.Physical,
            sourceDamage: 1,
            expectedDamage: 1,
            "wrong-channel final reduction retains the one-damage floor");
    }

    private static void CheckBossDamage(
        string rosterSpawnId,
        CombatDamageChannel channel,
        uint sourceDamage,
        uint expectedDamage,
        string description)
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            $"{description} fixture attaches");
        var target = FindMonster(fixture.Map, rosterSpawnId);
        var commit = CommitTypedDamage(
            fixture.Map,
            target,
            attackerCharacterId: 101,
            channel,
            sourceDamage,
            StartedAt.AddSeconds(1));

        Check.True(
            commit.Outcome ==
                MedusaPlayerMonsterDamageOutcome.AppliedMedusa &&
            commit.IsMedusaOwned &&
            commit.Resolution.Channel == channel &&
            commit.Resolution.Damage == expectedDamage &&
            commit.DamageResult is { } damage &&
            damage.BeforeHealth - damage.AfterHealth == expectedDamage &&
            commit.Defeat is null,
            description);
    }

    private static void CheckOwnerAmplifierOrdering()
    {
        CheckAmplifiedBossDamage(
            carrierRosterId: "Final-Pikeman-1",
            bossRosterId: "Stheno",
            CombatDamageChannel.Physical,
            sourceDamage: 100,
            expectedDamage: 1_000,
            "correct-channel physical amplifier applies before Stheno final damage");
        CheckAmplifiedBossDamage(
            carrierRosterId: "Final-Axeman-1",
            bossRosterId: "Stheno",
            CombatDamageChannel.Magic,
            sourceDamage: 100,
            expectedDamage: 100,
            "magical amplifier applies before Stheno wrong-channel reduction");

        var expired = CreateAttachmentFixture();
        Check.True(AttachAuthored(expired).IsAttached,
            "expired amplifier fixture attaches");
        ApplyCarrierHit(
            expired.Map,
            "Final-Pikeman-1",
            targetCharacterId: 101,
            StartedAt.AddSeconds(1));
        var stheno = FindMonster(expired.Map, "Stheno");
        var insideWindow = CommitTypedDamage(
            expired.Map,
            stheno,
            101,
            CombatDamageChannel.Physical,
            damage: 100,
            StartedAt.AddSeconds(30.999));
        var afterInside = expired.Map.TryGetMonsterSnapshot(
            stheno.ObjectId,
            out var refreshed)
            ? refreshed
            : throw new InvalidOperationException(
                "Stheno disappeared after amplifier-window damage.");
        var exactExpiry = CommitTypedDamage(
            expired.Map,
            afterInside,
            101,
            CombatDamageChannel.Physical,
            damage: 100,
            StartedAt.AddSeconds(31));
        Check.True(
            insideWindow.Resolution.Damage == 1_000 &&
            exactExpiry.Resolution.Damage == 100 &&
            expired.Map.TryGetMedusaOwnershipSnapshot(
                out var expiredOwner) &&
            expiredOwner.Run.LastObservedAt ==
                StartedAt.AddSeconds(31) &&
            expiredOwner.Mechanics.LastObservedAt ==
                StartedAt.AddSeconds(31) &&
            expiredOwner.Mechanics.Characters.Single()
                .ActiveEffects.IsEmpty,
            "amplifier applies 29.999 seconds after application and expires exactly 30 seconds after application");
        AssertCoupledAt(
            expired.Map,
            StartedAt.AddSeconds(31),
            "amplifier expiry damage");
    }

    private static void CheckAmplifiedBossDamage(
        string carrierRosterId,
        string bossRosterId,
        CombatDamageChannel channel,
        uint sourceDamage,
        uint expectedDamage,
        string description)
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            $"{description} fixture attaches");
        ApplyCarrierHit(
            fixture.Map,
            carrierRosterId,
            targetCharacterId: 101,
            StartedAt.AddSeconds(1));
        var boss = FindMonster(fixture.Map, bossRosterId);
        var commit = CommitTypedDamage(
            fixture.Map,
            boss,
            101,
            channel,
            sourceDamage,
            StartedAt.AddSeconds(2));
        Check.True(
            commit.Resolution.Damage == expectedDamage &&
            commit.DamageResult is { } applied &&
            applied.BeforeHealth - applied.AfterHealth == expectedDamage,
            description);
    }

    private static void ApplyCarrierHit(
        MapInstance map,
        string carrierRosterId,
        int targetCharacterId,
        DateTimeOffset committedAt)
    {
        var ownership = map.TryGetMedusaOwnershipSnapshot(out var snapshot)
            ? snapshot
            : throw new InvalidOperationException(
                "Carrier fixture lost Medusa ownership.");
        var carrier = Binding(ownership, carrierRosterId);
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                targetCharacterId,
                carrier.Identity.ObjectId,
                carrier.Identity.SpawnGeneration,
                committedAt,
                out var hit) &&
            hit.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            hit.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            $"{carrierRosterId} applies its owner-bound amplifier");
    }

    private static GameSessionContext CreateAdmittedDamageContext(
        MapInstance map,
        ClientSession session,
        int characterId)
    {
        var character = new GameCharacter
        {
            Id = characterId,
            AccountId = checked(10_000 + characterId),
            Name = $"MedusaDamage{characterId}",
            CreatedUtc = StartedAt.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = map.MapId,
            PositionX = 1,
            PositionZ = 1,
            Level = 120,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 10_000,
            MaxMp = 10_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
        return new(
            session,
            character.AccountId,
            character.Id,
            character.Name,
            map.RealmId,
            map.WorldInstanceId,
            map.MapId,
            ObjectId: 0x7A01,
            character,
            WorldReady: true,
            WorldRevision: 1)
        {
            WorldMembershipEpoch = 1,
            Ownership =
                MedusaEncounterMechanicsRuntime.CompatibilityOwnership
        };
    }

    private static object RequiredMapGate(
        MapInstance map,
        string fieldName) =>
        typeof(MapInstance).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(map) ??
        throw new InvalidOperationException(
            $"MapInstance gate {fieldName} was not found.");

    private static bool GateIsHeldByAnotherThread(object gate)
    {
        if (!Monitor.TryEnter(gate))
        {
            return true;
        }

        Monitor.Exit(gate);
        return false;
    }

    private static MonsterRuntimeSnapshot FindMonster(
        MapInstance map,
        string rosterSpawnId)
    {
        var ownership = map.TryGetMedusaOwnershipSnapshot(out var snapshot)
            ? snapshot
            : throw new InvalidOperationException(
                "Roster lookup requires explicit Medusa ownership.");
        var objectId = Binding(ownership, rosterSpawnId)
            .Identity.ObjectId;

        return map.TryGetMonsterSnapshot(objectId, out var monster)
            ? monster
            : throw new InvalidOperationException(
                $"Monster {rosterSpawnId} was not initialized.");
    }

    private static MedusaPlayerMonsterDamageCommit CommitTypedDamage(
        MapInstance map,
        MonsterRuntimeSnapshot target,
        int attackerCharacterId,
        CombatDamageChannel channel,
        uint damage,
        DateTimeOffset committedAt) => CommitSessionDamage(
        map,
        target,
        sessionCharacterId: attackerCharacterId,
        attackerCharacterId,
        target.SpawnGeneration,
        target.HealthRevision,
        committedAt,
        Resolution(channel, damage));

    private static MedusaPlayerMonsterDamageCommit CommitSessionDamage(
        MapInstance map,
        MonsterRuntimeSnapshot target,
        int sessionCharacterId,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        DateTimeOffset committedAt,
        in CombatResolution resolution)
    {
        var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        try
        {
            var context = CreateAdmittedDamageContext(
                map,
                session,
                sessionCharacterId);
            map.AddOrUpdate(context);
            return map.TryCommitPlayerMonsterDamageForSessionGuarded(
                session,
                target.ObjectId,
                target.RuntimeInstanceId,
                attackerCharacterId,
                expectedSpawnGeneration,
                expectedHealthRevision,
                new(
                    context.WorldInstanceId,
                    context.WorldRevision,
                    context.Ownership,
                    LifeRevision: 0,
                    context.WorldMembershipEpoch),
                committedAt,
                resolution);
        }
        finally
        {
            _ = map.Remove(session, out _);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static MedusaPlayerMonsterDamageCommit
        CommitTypedDamageWithExpectedIdentity(
            MapInstance map,
            MonsterRuntimeSnapshot target,
            int sessionCharacterId,
            int attackerCharacterId,
            uint expectedSpawnGeneration,
            ulong expectedHealthRevision,
            DateTimeOffset committedAt,
            in CombatResolution resolution) => CommitSessionDamage(
        map,
        target,
        sessionCharacterId,
        attackerCharacterId,
        expectedSpawnGeneration,
        expectedHealthRevision,
        committedAt,
        resolution);

    private static CombatResolution Resolution(
        CombatDamageChannel channel,
        uint damage) => new(
        FormulaVersion: 23,
        EventId: 0xC0111510UL,
        TargetOrder: 0,
        channel,
        CombatHitOutcome.Normal,
        damage,
        new CombatRollEvidence(10_000, 1, 0, 9_999),
        new CombatDamageEvidence(
            Attack: checked((int)Math.Min(damage, int.MaxValue)),
            EffectiveDefense: 0,
            AttackAfterDefense: damage,
            SkillCoreDamage: damage,
            DamageAfterTypedBonus: damage,
            CriticalBonusDamage: 0,
            DamageWithAppend: damage,
            DamageAfterReduction: damage,
            DamageAfterTakenIncrease: damage,
            DamageAfterAbsorption: damage));
}
