using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static readonly InjuryRankExpectation[] InjuryRanks =
    [
        new(320, 130, 12, 1_000),
        new(321, 131, 20, 1_000),
        new(322, 132, 12, 1_500),
        new(323, 133, 20, 1_500),
        new(324, 133, 20, 1_500),
        new(330, 130, 12, 1_000),
        new(331, 131, 20, 1_000),
        new(332, 132, 12, 1_500),
        new(333, 133, 20, 1_500),
        new(334, 133, 20, 1_500)
    ];

    private static async Task CheckInternalInjuryRuntimeAsync()
    {
        await CheckEveryInjuryRankRuntimeAsync();
        CheckPhysicalOnlyVulnerabilityFormula();
        // Expiry publication uses the live scheduler clock; keep the
        // integration timestamp current while core policy tests stay fixed.
        var now = DateTimeOffset.UtcNow;
        var meteor = AreaSkill(334);
        await using var fixture = await AreaFixture.CreateAsync();
        var actionRevision = FindApplyingAreaRevision(
            fixture,
            meteor,
            requireAllTargets: true);
        var decision = await fixture.ResolveAsync(
            meteor,
            () => actionRevision,
            now);
        Check.True(
            decision.Accepted &&
            decision.Combats.Count == 2 &&
            decision.Combats.All(static combat =>
                combat.HostileStatusApplication is
                {
                    Applied: true,
                    ActiveStatus.Definition.StatusId: 133
                }),
            "Spear/Meteor Injury lands only as a committed post-hit status proc");

        var firstCombat = decision.Combats.Single(combat =>
            combat.Target?.CharacterId == fixture.Dummies[0].Id);
        var attackerCombat = CombatCharacterStatsAdapter.FromCharacter(
            fixture.Attacker);
        var baselineTarget = CombatCharacterStatsAdapter.ToTarget(
            fixture.Dummies[0].Level,
            fixture.Dummies[0].CalculatedStats!);
        var triggeringBaseline = PlayerCombatRules.ResolvePvpSkillDamage(
            attackerCombat,
            baselineTarget,
            TrainingDummyDamageSkillPolicy.Snapshot(meteor),
            firstCombat.Resolution.EventId,
            firstCombat.Resolution.TargetOrder);
        Check.Equal(
            triggeringBaseline.Damage,
            firstCombat.Resolution.Damage,
            "the triggering Meteor hit is resolved before Injury becomes active");

        var snapshot = fixture.Registry
            .CaptureTrainingDummyHostileStatusSnapshot(
                fixture.FirstDummySocket.Session,
                now);
        Check.True(
            snapshot.ActiveStatuses is
            [
                {
                    Definition.StatusId: 133,
                    Definition.Kind: 5,
                    Definition.Priority: 4
                }
            ] &&
            snapshot.ActiveStatuses[0].RemainingSeconds(now) == 20,
            "active Internal Injury preserves stock status 133 and its fixed 20-second timer");

        Check.True(
            fixture.Registry.TryGetRuntimeIncomingDamageMitigation(
                fixture.FirstDummySocket.Session,
                now.AddSeconds(1),
                out var mitigation) &&
            mitigation.PhysicalDamageTakenIncreaseBasisPoints == 1_500 &&
            mitigation.MagicDamageTakenIncreaseBasisPoints == 0,
            "authoritative Injury exposes +15-percent physical-only incoming damage");

        var spear = SpearHit();
        var firstDummyObjectId = fixture.Registry.GetRequiredPlayerObjectId(
            fixture.FirstDummySocket.Session);
        var scalarRevision = FindHittingScalarRevision(
            fixture,
            spear,
            baselineTarget with
            {
                PhysicalDamageTakenIncreaseBasisPoints = 1_500
            });
        var scalar = await fixture.Registry
            .ResolveTrainingDummyDamageScalarAsync(
                fixture.AttackerSocket.Session,
                LocalPlayerObjectId,
                firstDummyObjectId,
                TrainingSkillCastPacket(
                    checked((uint)spear.SkillId),
                    firstDummyObjectId),
                spear,
                () => scalarRevision,
                now.AddSeconds(1),
                CancellationToken.None);
        var noInjury = PlayerCombatRules.ResolvePvpSkillDamage(
            CombatCharacterStatsAdapter.FromCharacter(fixture.Attacker),
            baselineTarget,
            TrainingDummyDamageSkillPolicy.Snapshot(spear),
            scalar.Combat.Resolution.EventId);
        var withInjury = PlayerCombatRules.ResolvePvpSkillDamage(
            CombatCharacterStatsAdapter.FromCharacter(fixture.Attacker),
            baselineTarget with
            {
                PhysicalDamageTakenIncreaseBasisPoints = 1_500
            },
            TrainingDummyDamageSkillPolicy.Snapshot(spear),
            scalar.Combat.Resolution.EventId);
        Check.True(
            scalar.Accepted &&
            scalar.Combat.Resolution.Hit &&
            scalar.Combat.Resolution.Damage == withInjury.Damage &&
            withInjury.Damage > noInjury.Damage &&
            withInjury.Evidence.DamageAfterTakenIncrease >
                withInjury.Evidence.DamageAfterReduction,
            "the next physical hit consumes the active 15-percent Injury modifier before absorption");

        fixture.Registry.AdvancePlayerLifeRevision(
            fixture.FirstDummySocket.Session);
        Check.True(
            fixture.Registry
                .CaptureTrainingDummyHostileStatusSnapshot(
                    fixture.FirstDummySocket.Session,
                    now.AddSeconds(2))
                .ActiveStatuses.Count == 0,
            "death/revive life revision clears authoritative Injury immediately");
    }

    private static async Task CheckEveryInjuryRankRuntimeAsync()
    {
        foreach (var expected in InjuryRanks)
        {
            var now = DateTimeOffset.UtcNow;
            var skill = AreaSkill(expected.SkillId);
            await using var fixture = await AreaFixture.CreateAsync();
            var initialMana = fixture.Attacker.CurrentMp;
            var actionRevision = FindApplyingAreaRevision(
                fixture,
                skill,
                requireAllTargets: true);
            var decision = await fixture.ResolveAsync(
                skill,
                () => actionRevision,
                now);

            Check.True(
                decision.Accepted &&
                decision.Combats.Count == fixture.Dummies.Count &&
                decision.Combats.All(combat =>
                    combat.HostileStatusApplication is
                    {
                        Applied: true,
                        ActiveStatus: { } active
                    } &&
                    active.Definition.StatusId == expected.StatusId &&
                    active.Definition.Duration ==
                        TimeSpan.FromSeconds(expected.DurationSeconds) &&
                    active.Definition.
                        PhysicalDamageTakenIncreaseBasisPoints ==
                        expected.PhysicalTakenBasisPoints) &&
                fixture.Attacker.CurrentMp == initialMana - skill.Mp,
                $"Injury skill {expected.SkillId} commits its stock rank and MP");

            var snapshot = fixture.Registry
                .CaptureTrainingDummyHostileStatusSnapshot(
                    fixture.FirstDummySocket.Session,
                    now);
            var active = snapshot.ActiveStatuses.Single();
            Check.True(
                active.Definition.StatusId == expected.StatusId &&
                active.RemainingSeconds(now) == expected.DurationSeconds &&
                fixture.Registry.TryGetRuntimeIncomingDamageMitigation(
                    fixture.FirstDummySocket.Session,
                    now.AddMilliseconds(1),
                    out var mitigation) &&
                mitigation.PhysicalDamageTakenIncreaseBasisPoints ==
                    expected.PhysicalTakenBasisPoints &&
                mitigation.MagicDamageTakenIncreaseBasisPoints == 0,
                $"Injury skill {expected.SkillId} exposes its stock duration and effect");

            var replayCalls = 0;
            var replay = await fixture.ResolveAsync(
                skill,
                () => ++replayCalls,
                now.AddMilliseconds(skill.Cooldown.TotalMilliseconds - 1));
            Check.True(
                replay.RejectionReason ==
                    TrainingDummySkillRejectionReason.CooldownActive &&
                replay.CooldownReadyAt == now + skill.Cooldown &&
                replayCalls == 0 &&
                fixture.Attacker.CurrentMp == initialMana - skill.Mp,
                $"Injury skill {expected.SkillId} retains its published cooldown");
        }
    }

    private static void CheckPhysicalOnlyVulnerabilityFormula()
    {
        var attacker = new CombatAttackerStats
        {
            Level = 160,
            Profession = 3,
            PhysicalAttack = 1_000,
            MagicAttack = 1_000,
            Hit = 10_000
        };
        var baseline = new CombatTargetStats
        {
            Level = 160,
            PhysicalDefense = 100,
            MagicDefense = 100
        };
        var injury = baseline with
        {
            PhysicalDamageTakenIncreaseBasisPoints = 1_500
        };
        var physicalBaseline = AuthoredCombatV2.ResolveSkillDamageForOutcome(
            attacker,
            baseline,
            property: 0,
            powerAdjustment: 0m,
            flatPower: 0m,
            CombatHitOutcome.Normal);
        var physicalInjury = AuthoredCombatV2.ResolveSkillDamageForOutcome(
            attacker,
            injury,
            property: 0,
            powerAdjustment: 0m,
            flatPower: 0m,
            CombatHitOutcome.Normal);
        var magicBaseline = AuthoredCombatV2.ResolveSkillDamageForOutcome(
            attacker,
            baseline,
            property: 1,
            powerAdjustment: 0m,
            flatPower: 0m,
            CombatHitOutcome.Normal);
        var magicInjury = AuthoredCombatV2.ResolveSkillDamageForOutcome(
            attacker,
            injury,
            property: 1,
            powerAdjustment: 0m,
            flatPower: 0m,
            CombatHitOutcome.Normal);
        Check.True(
            physicalInjury.Damage > physicalBaseline.Damage &&
            magicInjury.Damage == magicBaseline.Damage,
            "Internal Injury increases physical damage only and never magic damage");
    }

    private static long FindApplyingAreaRevision(
        AreaFixture fixture,
        SkillCombatDefinition skill,
        bool requireAllTargets)
    {
        var attacker = fixture.Attacker.CalculatedStats!;
        for (var revision = 1L; revision <= 20_000; revision++)
        {
            var results = fixture.Dummies.Select(target =>
            {
                var eventId = CombatEventIdentity.ForPlayerSkill(
                    fixture.Attacker.Id,
                    target.Id,
                    fixture.Attacker.VitalsRevision,
                    target.VitalsRevision,
                    revision,
                    checked((uint)skill.SkillId));
                var damage = PlayerCombatRules.ResolvePvpSkillDamage(
                    CombatCharacterStatsAdapter.FromCharacter(
                        fixture.Attacker),
                    CombatCharacterStatsAdapter.ToTarget(
                        target.Level,
                        target.CalculatedStats!),
                    TrainingDummyDamageSkillPolicy.Snapshot(skill),
                    eventId);
                var proc = HostileStatusProcPolicy.Evaluate(
                    new HostileStatusProcRatings(
                        fixture.Attacker.Level,
                        target.Level,
                        attacker.Hit,
                        target.CalculatedStats!.Dodge,
                        attacker.StatusHit,
                        target.CalculatedStats.StatusResistance),
                    eventId,
                    targetOrder: 0);
                return damage.Hit && damage.Damage > 0 && proc.Applied;
            }).ToArray();
            if (requireAllTargets
                    ? results.All(static value => value)
                    : results.Any(static value => value))
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "Could not find deterministic applying Injury revision.");
    }

    private static long FindHittingScalarRevision(
        AreaFixture fixture,
        in SkillCombatDefinition skill,
        in CombatTargetStats target)
    {
        for (var revision = 20_001L; revision <= 40_000; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerSkill(
                fixture.Attacker.Id,
                fixture.Dummies[0].Id,
                fixture.Attacker.VitalsRevision,
                fixture.Dummies[0].VitalsRevision,
                revision,
                checked((uint)skill.SkillId));
            if (PlayerCombatRules.ResolvePvpSkillDamage(
                    CombatCharacterStatsAdapter.FromCharacter(
                        fixture.Attacker),
                    target,
                    TrainingDummyDamageSkillPolicy.Snapshot(skill),
                    eventId).Hit)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "Could not find deterministic hitting scalar revision.");
    }

    private readonly record struct InjuryRankExpectation(
        int SkillId,
        uint StatusId,
        uint DurationSeconds,
        int PhysicalTakenBasisPoints);
}
