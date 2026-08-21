using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CombatSecondaryProjectionChecks
{
    public const string CheckName =
        "Typed Holy Spirit and secondary-combat projection";

    public static Task RunAsync()
    {
        CheckProjectionContract();
        CheckProjectionAuthorityFences();
        CheckTypedTargetHydration();
        CheckSecondaryEffects();
        CheckSecondaryCommitFence();
        CheckCommittedLifeAbsorption();
        CheckWitheredLifeAbsorption();
        CheckSecondaryPublicationOrder();
        CheckMonsterDamageChannels();
        CheckAttackIntervalWireProjection();
        return Task.CompletedTask;
    }

    private static void CheckProjectionContract()
    {
        var sql = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        foreach (var expected in new[]
                 {
                     "WHEN 9 THEN 'physical_damage_reduction'",
                     "WHEN 10 THEN 'magic_damage_reduction'",
                     "WHEN 11 THEN 'physical_flat_absorption'",
                     "WHEN 12 THEN 'magic_flat_absorption'",
                     "WHEN 13 THEN 'critical_damage_reduction'",
                     "WHEN 14 THEN 'critical_damage_flat_reduction'",
                     "WHEN 19 THEN 'damage_rebound'",
                     "WHEN 20 THEN 'damage_rebound_flat'",
                     "(27, 'life_absorption')",
                     "(28, 'damage_rebound')"
                 })
        {
            Check.True(
                sql.Contains(expected, StringComparison.Ordinal),
                $"calculated-stat SQL contains typed mapping {expected}");
        }

        Check.True(
            sql.Contains(
                "WHEN socket.effect_id IN (9,10,11,12,13,14,19,20)",
                StringComparison.Ordinal) &&
            sql.Contains("THEN 'damage_absorb'", StringComparison.Ordinal),
            "cooled Holy Spirits retain the legacy aggregate packet field");
        Check.True(
            sql.Contains("equipment.item_grade::integer", StringComparison.Ordinal) &&
            sql.Contains("template.stats->>'AttackSpeed'", StringComparison.Ordinal) &&
            sql.Contains("template.stats->>'AttackRadius'", StringComparison.Ordinal) &&
            sql.Contains("1500", StringComparison.Ordinal) &&
            sql.Contains("1.7::real", StringComparison.Ordinal),
            "weapon cadence and range use pinned grade vectors and fallbacks");
        Check.True(
            EquipmentSlots.Weapon == 10 &&
            sql.Contains(
                "AND equipment.slot_index = 10",
                StringComparison.Ordinal),
            "weapon cadence only reads the authoritative durable weapon slot");
    }

    private static void CheckTypedTargetHydration()
    {
        var stats = new CharacterStats
        {
            DamageAbsorb = 9_999,
            PhysicalDamageReduction = 101,
            MagicDamageReduction = 202,
            CriticalDamageReduction = 303,
            PhysicalFlatAbsorption = 11,
            MagicFlatAbsorption = 22,
            CriticalDamageFlatReduction = 33,
            DamageRebound = 404,
            DamageReboundFlat = 44
        };
        var target = CombatCharacterStatsAdapter.ToTarget(50, stats);

        Check.Equal(101, target.PhysicalDamageReductionBasisPoints,
            "physical reduction hydrates as basis points");
        Check.Equal(202, target.MagicDamageReductionBasisPoints,
            "magical reduction hydrates as basis points");
        Check.Equal(303, target.CriticalDamageReductionBasisPoints,
            "critical reduction hydrates as basis points");
        Check.Equal(11, target.PhysicalFlatAbsorption,
            "physical flat absorption stays typed");
        Check.Equal(22, target.MagicFlatAbsorption,
            "magical flat absorption stays typed");
        Check.Equal(33, target.CriticalDamageFlatReduction,
            "critical flat reduction stays typed");
        Check.Equal(404, target.DamageReboundBasisPoints,
            "rebound percent hydrates as basis points");
        Check.Equal(44, target.DamageReboundFlat,
            "rebound flat stays typed");
        Check.True(
            target.PhysicalFlatAbsorption != stats.DamageAbsorb &&
            target.MagicFlatAbsorption != stats.DamageAbsorb,
            "legacy aggregate does not double-count typed absorption");
    }

    private static void CheckSecondaryEffects()
    {
        var attacker = new CombatAttackerStats
        {
            LifeAbsorptionBasisPoints = 1_500,
            LifeAbsorptionFlat = 7
        };
        var target = new CombatTargetStats
        {
            DamageReboundBasisPoints = 2_500,
            DamageReboundFlat = 3
        };
        var direct = CombatSecondaryEffectPolicy.Resolve(
            100,
            attacker,
            target);

        Check.Equal(22u, direct.LifeAbsorptionHealing,
            "life absorption combines percentage and fixed on-hit healing");
        Check.Equal(28u, direct.ReboundDamage,
            "rebound combines basis points and flat damage");
        Check.True(
            direct.ReboundProvenance == CombatDamageProvenance.Rebound,
            "rebound output carries non-recursive provenance");

        var recursive = CombatSecondaryEffectPolicy.Resolve(
            direct.ReboundDamage,
            attacker,
            target,
            CombatDamageProvenance.Rebound);
        Check.Equal(0u, recursive.LifeAbsorptionHealing,
            "rebound cannot trigger life absorption recursively");
        Check.Equal(0u, recursive.ReboundDamage,
            "rebound cannot trigger another rebound");
        Check.Equal(
            10,
            CombatSecondaryEffectPolicy
                .ClampLifeAbsorptionToMissingHealth(
                    requestedHealing: 15,
                    currentHealth: 90,
                    maximumHealth: 100),
            "life absorption is capped by committed missing HP");
        Check.Equal(
            0,
            CombatSecondaryEffectPolicy
                .ClampLifeAbsorptionToMissingHealth(
                    requestedHealing: 15,
                    currentHealth: 0,
                    maximumHealth: 100),
            "life absorption cannot revive its source");
    }

    private static void CheckSecondaryCommitFence()
    {
        var ledger = new CombatSecondaryEffectCommitLedger(capacity: 2);
        var first = new CombatSecondaryEffectCommitKey(
            CombatSecondaryEffectCommitKind.LifeAbsorption,
            CharacterId: 1,
            MonsterObjectId: 10,
            MonsterSpawnGeneration: 1,
            CombatEventId: 100);
        var second = first with { CombatEventId = 101 };
        var third = first with { CombatEventId = 102 };
        Check.True(
            ledger.TryClaim(first) &&
            !ledger.TryClaim(first) &&
            ledger.TryClaim(second) &&
            ledger.TryClaim(third) &&
            ledger.Count == 2 &&
            ledger.TryClaim(first),
            "secondary-effect commit fence deduplicates and evicts within a bounded ledger");

        var packet = GameSessionRegistry.BuildMonsterReboundPacket(
            attackerObjectId: 0x1448,
            attackerX: 1f,
            attackerZ: 2f,
            monsterObjectId: 99,
            reboundDamage: 123,
            attackSelector: 3);
        Check.Equal(
            (byte)CombatHitOutcome.Normal,
            packet[29],
            "monster rebound packet is pinned to a terminal normal outcome");
    }

    private static void CheckCommittedLifeAbsorption()
    {
        var character = new GameCharacter
        {
            Id = 7,
            CurrentHp = 90,
            MaxHp = 100,
            CalculatedStats = new CharacterStats
            {
                LifeAbsorption = 5_000,
                LifeAbsorptionFlat = 3
            }
        };
        var committer = new PveLifeAbsorptionCommitter(
            ledgerCapacity: 4);
        var hit = new PveCommittedMonsterDamage(
            CombatEventId: 11,
            MonsterObjectId: 99,
            MonsterSpawnGeneration: 1,
            AppliedDamage: 40);
        var committed = committer.Commit(character, [hit]);
        Check.True(
            committed is
            {
                ClaimedHitCount: 1,
                RequestedHealing: 23,
                AppliedHealing: 10,
                BeforeHealth: 90,
                AfterHealth: 100,
                BeforeVitalsRevision: 0,
                AfterVitalsRevision: 1
            } &&
            character.CurrentHp == 100 &&
            character.VitalsRevision == 1,
            "committed life absorption caps at missing HP and advances vitals once");

        var replay = committer.Commit(character, [hit]);
        Check.True(
            replay.ClaimedHitCount == 0 &&
            replay.AppliedHealing == 0 &&
            character.CurrentHp == 100 &&
            character.VitalsRevision == 1,
            "replayed committed hit cannot heal or advance vitals twice");

        character.CurrentHp = 50;
        var areaCommit = committer.Commit(
            character,
            [
                hit with
                {
                    CombatEventId = 21,
                    MonsterObjectId = 100,
                    AppliedDamage = 11
                },
                hit with
                {
                    CombatEventId = 22,
                    MonsterObjectId = 101,
                    AppliedDamage = 11
                }
            ]);
        Check.True(
            areaCommit is
            {
                ClaimedHitCount: 2,
                RequestedHealing: 16,
                AppliedHealing: 16,
                BeforeHealth: 50,
                AfterHealth: 66
            } &&
            character.VitalsRevision == 2,
            "area life absorption rounds and commits independently per hit");

        var areaReplay = committer.Commit(
            character,
            [
                hit with
                {
                    CombatEventId = 21,
                    MonsterObjectId = 100,
                    AppliedDamage = 11
                },
                hit with
                {
                    CombatEventId = 22,
                    MonsterObjectId = 101,
                    AppliedDamage = 11
                }
            ]);
        Check.True(
            areaReplay.ClaimedHitCount == 0 &&
            areaReplay.AppliedHealing == 0 &&
            character.CurrentHp == 66 &&
            character.VitalsRevision == 2,
            "fixed on-hit recovery applies once per committed hit and never on replay");

        character.CurrentHp = 0;
        var dead = committer.Commit(
            character,
            [hit with { CombatEventId = 12 }]);
        Check.True(
            dead.ClaimedHitCount == 1 &&
            dead.AppliedHealing == 0 &&
            character.CurrentHp == 0,
            "life absorption cannot revive a dead attacker");
    }

    private static void CheckSecondaryPublicationOrder()
    {
        var root = FindRepositoryRoot();
        foreach (var (file, primaryLabel) in new[]
                 {
                     ("GameClientHandler.MovementCombat.cs", "\"BasicAttackWorld\""),
                     ("GameClientHandler.CombatEcsBasic.cs", "\"BasicAttackWorld\""),
                     ("GameClientHandler.CombatSkill.cs", "await PublishLegacyHostileMonsterSkillHitAsync("),
                     ("GameClientHandler.CombatEcsSkill.cs", "\"SkillDamageWorld\""),
                     ("GameClientHandler.CombatArea.cs", "\"AreaSkill\","),
                     ("GameClientHandler.CombatEcsArea.cs", "\"AreaSkill\",")
                 })
        {
            var source = File.ReadAllText(Path.Combine(
                root,
                "src",
                "Godswar.Server",
                "Game",
                file));
            var commit = source.IndexOf(
                "CommitPveLifeAbsorption(",
                StringComparison.Ordinal);
            var primary = source.IndexOf(
                primaryLabel,
                StringComparison.Ordinal);
            var healing = source.IndexOf(
                "await PublishPveLifeAbsorptionAsync(",
                StringComparison.Ordinal);
            var reward = source.IndexOf(
                "await PublishMonsterKillRewardAsync(",
                StringComparison.Ordinal);
            Check.True(
                commit >= 0 &&
                primary > commit &&
                healing > primary &&
                reward > healing,
                $"{file} commits with damage and publishes hit, heal, then reward");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Godswar repository root.");
    }

    private static void CheckMonsterDamageChannels()
    {
        var target = new GameCharacter
        {
            Level = 20,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CalculatedStats = new CharacterStats
            {
                Level = 20,
                PhysicalDefense = 100_000,
                MagicDefense = 0
            }
        };
        var physicalProfile = MonsterCombatProfileCatalog.Resolve(
            20,
            MonsterAttackDamageKind.Physical);
        var magicalProfile = MonsterCombatProfileCatalog.Resolve(
            20,
            MonsterAttackDamageKind.Magical);
        var combatEventId = FindSharedHitEventId(
            physicalProfile,
            magicalProfile,
            target);
        var physical = MonsterIncomingCombatPolicy.ResolveAttack(
            physicalProfile,
            target,
            default,
            combatEventId);
        var magical = MonsterIncomingCombatPolicy.ResolveAttack(
            magicalProfile,
            target,
            default,
            combatEventId);

        Check.True(
            physical.Channel == CombatDamageChannel.Physical &&
            magical.Channel == CombatDamageChannel.Magic,
            "monster AttackType selects the matching damage channel");
        Check.True(
            magical.Damage > physical.Damage,
            "magical monsters use magic attack against magic defense");

        var mitigation = new RuntimeIncomingDamageMitigation(
            0m,
            0.5m,
            0,
            10);
        var mitigated = MonsterIncomingCombatPolicy.ResolveAttack(
            magicalProfile,
            target,
            mitigation,
            combatEventId);
        Check.True(
            mitigated.Damage < magical.Damage,
            "runtime magic defense and magic reduction mitigate magic attacks");

        var repeated = MonsterIncomingCombatPolicy.ResolveAttack(
            magicalProfile,
            target,
            mitigation,
            combatEventId);
        Check.True(
            repeated == mitigated,
            "monster hit, critical, and damage rolls replay by attack event ID");

        var missEventId = FindEventId(
            magicalProfile,
            target,
            shouldHit: false);
        var miss = MonsterIncomingCombatPolicy.ResolveAttack(
            magicalProfile,
            target,
            default,
            missEventId);
        Check.True(
            miss.Outcome == CombatHitOutcome.Miss &&
            miss.Damage == 0 &&
            miss.CapturedDamageValue == uint.MaxValue,
            "monster misses retain zero authoritative damage and the captured wire sentinel");
    }

    private static ulong FindSharedHitEventId(
        in MonsterCombatProfile first,
        in MonsterCombatProfile second,
        GameCharacter target)
    {
        for (ulong eventId = 1; eventId <= 100_000; eventId++)
        {
            if (MonsterIncomingCombatPolicy.ResolveAttack(
                    first,
                    target,
                    default,
                    eventId).Hit &&
                MonsterIncomingCombatPolicy.ResolveAttack(
                    second,
                    target,
                    default,
                    eventId).Hit)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            "Could not locate a shared deterministic monster hit event.");
    }

    private static ulong FindEventId(
        in MonsterCombatProfile profile,
        GameCharacter target,
        bool shouldHit)
    {
        for (ulong eventId = 1; eventId <= 100_000; eventId++)
        {
            if (MonsterIncomingCombatPolicy.ResolveAttack(
                    profile,
                    target,
                    default,
                    eventId).Hit == shouldHit)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the requested deterministic monster outcome.");
    }

    private static void CheckAttackIntervalWireProjection()
    {
        var character = new GameCharacter
        {
            Name = "Cadence",
            CurrentHp = 100,
            MaxHp = 100,
            CalculatedStats = new CharacterStats
            {
                BasicAttackIntervalMilliseconds = 2_300,
                BasicAttackRange = 2.5f,
                DamageAbsorb = 777
            }
        };
        var packet = PacketBuilder.PlayerStatusUpdate(character, 0x1448);

        Check.Equal(
            2_300,
            (int)BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(114, 2)),
            "10166 short cadence field uses authored interval");
        Check.Equal(
            2_300u,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(224, 4)),
            "10166 dword cadence field duplicates authored interval");
        Check.Equal(
            777,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(200, 4)),
            "10166 retains the legacy aggregate DamageAbsorb field");
    }
}
