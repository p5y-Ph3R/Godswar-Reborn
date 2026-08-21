using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static async Task CheckStatusOnlyCastTransactionAsync()
    {
        if (!TrainingDummyHostileStatusSkillCatalog.TryGet(
                74,
                out var stun) ||
            !GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                74,
                out var skill))
        {
            throw new InvalidOperationException(
                "Rank-five Stun definitions are missing.");
        }
        var now = DateTimeOffset.UtcNow;

        await using (var fixture = await Fixture.CreateAsync(
                         attacker: HostileStatusWarrior()))
        {
            var revision = FindStatusOnlyRevision(
                fixture,
                stun,
                applied: true);
            var initialMana = fixture.Attacker.CurrentMp;
            var interruptionClaimed = false;
            var claimObservedNoActiveStatus = false;
            var resolution = fixture.Registry
                .ResolveTrainingDummyHostileStatusCastAsync(
                    fixture.AttackerSocket.Session,
                    LocalPlayerObjectId,
                    fixture.TargetObjectId,
                    skill,
                    stun,
                    () => revision,
                    now,
                    CancellationToken.None,
                    (target, applied) =>
                    {
                        interruptionClaimed =
                            ReferenceEquals(
                                target.Session,
                                fixture.TargetSocket.Session) &&
                            applied == stun;
                        claimObservedNoActiveStatus =
                            fixture.Registry
                                .CaptureTrainingDummyHostileStatusSnapshot(
                                    target.Session,
                                    now)
                                .ActiveStatuses.Count == 0;
                    });
            Check.True(
                interruptionClaimed && claimObservedNoActiveStatus,
                "an applied control claims interruption before its active " +
                "status is written at the registry commit boundary");
            var decision = await resolution;
            Check.True(
                decision.Accepted &&
                decision.Targets is
                [
                    {
                        Application.Applied: true,
                        Application.ActiveStatus.Definition.StatusId: 331
                    }
                ] &&
                fixture.Attacker.CurrentMp ==
                    initialMana - stun.ManaCost &&
                (fixture.Registry.GetTrainingDummyHostileControl(
                     fixture.TargetSocket.Session,
                     now) &
                 (HostileStatusControlFlags.NonMoving |
                  HostileStatusControlFlags.NonAttackUsing)) ==
                    (HostileStatusControlFlags.NonMoving |
                     HostileStatusControlFlags.NonAttackUsing),
                "status-only Stun atomically spends MP and commits its continuous controls");

            var revisionCalls = 0;
            var replay = await fixture.Registry
                .ResolveTrainingDummyHostileStatusCastAsync(
                    fixture.AttackerSocket.Session,
                    LocalPlayerObjectId,
                    fixture.TargetObjectId,
                    skill,
                    stun,
                    () => ++revisionCalls,
                    now.AddSeconds(1),
                    CancellationToken.None);
            Check.True(
                replay.RejectionReason ==
                    TrainingDummySkillRejectionReason.CooldownActive &&
                revisionCalls == 0 &&
                fixture.Attacker.CurrentMp ==
                    initialMana - stun.ManaCost,
                "status-only cooldown replay spends no revision or MP");
        }

        await using (var miss = await Fixture.CreateAsync(
                         attacker: HostileStatusWarrior(
                             id: 8_602,
                             accountId: 8_602)))
        {
            var revision = FindStatusOnlyRevision(
                miss,
                stun,
                applied: false);
            var initialMana = miss.Attacker.CurrentMp;
            var decision = await miss.Registry
                .ResolveTrainingDummyHostileStatusCastAsync(
                    miss.AttackerSocket.Session,
                    LocalPlayerObjectId,
                    miss.TargetObjectId,
                    skill,
                    stun,
                    () => revision,
                    now,
                    CancellationToken.None);
            Check.True(
                decision.Accepted &&
                decision.Targets is
                [{ Application.Disposition:
                    HostileStatusApplicationDisposition.ProcMiss }] &&
                miss.Attacker.CurrentMp ==
                    initialMana - stun.ManaCost &&
                miss.Registry.CaptureTrainingDummyHostileStatusSnapshot(
                        miss.TargetSocket.Session,
                        now)
                    .ActiveStatuses.Count == 0,
                "a deterministic status miss still commits cast resources without creating control state");
        }

        await using (var ordinary = await Fixture.CreateAsync(
                         attacker: HostileStatusWarrior(
                             id: 8_603,
                             accountId: 8_603),
                         target: Player(
                             8_604,
                             8_604,
                             "OrdinaryStatusTarget",
                             0,
                             1)))
        {
            var initialMana = ordinary.Attacker.CurrentMp;
            var revisionCalls = 0;
            var decision = await ordinary.Registry
                .ResolveTrainingDummyHostileStatusCastAsync(
                    ordinary.AttackerSocket.Session,
                    LocalPlayerObjectId,
                    ordinary.TargetObjectId,
                    skill,
                    stun,
                    () => ++revisionCalls,
                    now,
                    CancellationToken.None);
            Check.True(
                !decision.Handled &&
                revisionCalls == 0 &&
                ordinary.Attacker.CurrentMp == initialMana,
                "status-only adapter returns unchanged ordinary PvE/PvP routing when no exact dummy is targeted");
        }
    }

    private static GameCharacter HostileStatusWarrior(
        int id = 8_601,
        int accountId = 8_601)
    {
        var warrior = Player(
            id,
            accountId,
            "StatusWarrior",
            map: 0,
            camp: 0);
        warrior.Profession = 0;
        warrior.CalculatedStats = new CharacterStats
        {
            CharacterId = id,
            AccountId = accountId,
            Name = warrior.Name,
            Profession = 0,
            Level = warrior.Level,
            CurrentHp = warrior.CurrentHp,
            MaxHp = warrior.MaxHp,
            CurrentMp = warrior.CurrentMp,
            MaxMp = warrior.MaxMp,
            PhysicalAttack = 1_000,
            Hit = 5_000,
            StatusHit = 500,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return warrior;
    }

    private static long FindStatusOnlyRevision(
        Fixture fixture,
        in HostileStatusEffectDefinition definition,
        bool applied)
    {
        var attacker = fixture.Attacker.CalculatedStats!;
        var target = fixture.Target.CalculatedStats!;
        for (var revision = 1L; revision <= 20_000; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerSkill(
                fixture.Attacker.Id,
                fixture.Target.Id,
                fixture.Attacker.VitalsRevision,
                fixture.Target.VitalsRevision,
                revision,
                checked((uint)definition.SkillId));
            var proc = HostileStatusProcPolicy.Evaluate(
                new HostileStatusProcRatings(
                    fixture.Attacker.Level,
                    fixture.Target.Level,
                    attacker.Hit,
                    target.Dodge,
                    attacker.StatusHit,
                    target.StatusResistance),
                eventId,
                targetOrder: 0);
            if (proc.Applied == applied)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "Could not find requested deterministic status proc outcome.");
    }
}
