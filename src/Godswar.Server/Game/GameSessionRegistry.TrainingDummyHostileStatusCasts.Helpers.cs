using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IReadOnlyList<GameSessionContext> ResolveStatusOnlyTargetSnapshots(
        ClientSession attackingSession,
        GameSessionContext route,
        uint targetObjectId,
        in HostileStatusEffectDefinition definition)
    {
        if (definition.TargetMode ==
            HostileStatusTargetMode.SingleTarget)
        {
            return TryGetCurrentWorldSessionByObjectId(
                    attackingSession,
                    route.MapId,
                    targetObjectId,
                    out var target) &&
                _trainingDummies.Contains(target.Character)
                    ? [target]
                    : [];
        }

        return GetWorldInstanceSessions(route.WorldInstanceId)
            .Where(candidate =>
                !ReferenceEquals(candidate.Session, attackingSession) &&
                _trainingDummies.Contains(candidate.Character))
            .OrderBy(static candidate => candidate.ObjectId)
            .ToArray();
    }

    private List<GameSessionContext> ResolveCurrentStatusOnlyTargetsLocked(
        GameSessionContext attacker,
        IReadOnlyList<GameSessionContext> snapshots,
        in SkillCombatDefinition skill,
        in HostileStatusEffectDefinition definition,
        out TrainingDummySkillRejectionReason failure)
    {
        failure = TrainingDummySkillRejectionReason.None;
        var targets = new List<GameSessionContext>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (!_sessions.TryGetValue(snapshot.Session, out var current) ||
                !current.WorldReady ||
                current.WorldInstanceId != attacker.WorldInstanceId ||
                current.WorldRevision != snapshot.WorldRevision ||
                current.ObjectId != snapshot.ObjectId ||
                !ReferenceEquals(current.Character, snapshot.Character) ||
                !_trainingDummies.Contains(current.Character))
            {
                failure = TrainingDummySkillRejectionReason.
                    StaleWorldOwnership;
                return [];
            }
            if (current.Character.CurrentHp <= 0)
            {
                continue;
            }

            var inRange = definition.TargetMode ==
                HostileStatusTargetMode.SelfCenteredArea
                    ? SkillCombatResolver.IsWithinArea(
                        attacker.Character.PositionX,
                        attacker.Character.PositionZ,
                        current.Character.PositionX,
                        current.Character.PositionZ,
                        skill)
                    : PlayerCombatRules.IsWithinSkillRange(
                        attacker.Character.PositionX,
                        attacker.Character.PositionZ,
                        current.Character.PositionX,
                        current.Character.PositionZ,
                        TrainingDummyDamageSkillPolicy.Snapshot(skill));
            if (inRange)
            {
                targets.Add(current);
            }
            else if (definition.TargetMode ==
                     HostileStatusTargetMode.SingleTarget)
            {
                failure = TrainingDummySkillRejectionReason.OutOfRange;
                return [];
            }
        }

        targets.Sort(static (left, right) =>
            left.ObjectId.CompareTo(right.ObjectId));
        return targets;
    }

    private static bool IsExactStatusOnlyDefinition(
        in SkillCombatDefinition skill,
        in HostileStatusEffectDefinition definition)
    {
        if (definition.Trigger !=
                HostileStatusApplicationTrigger.CommittedCast ||
            !TrainingDummyHostileStatusSkillCatalog.TryGet(
                skill.SkillId,
                out var published) ||
            published != definition ||
            definition.SkillId != skill.SkillId ||
            definition.ManaCost != skill.Mp ||
            definition.Cooldown != skill.Cooldown ||
            skill.Power1 != -1m ||
            skill.Power2 != 0m)
        {
            return false;
        }

        return definition.TargetMode switch
        {
            HostileStatusTargetMode.SingleTarget =>
                skill.Target == 44 &&
                skill.AffectObj == 28 &&
                skill.Distance == definition.Range &&
                skill.Range == 0f,
            HostileStatusTargetMode.SelfCenteredArea =>
                skill.Target == 1 &&
                skill.AffectObj == 28 &&
                skill.Distance == 0f &&
                skill.Range == definition.Range,
            _ => false
        };
    }

    private bool IsTrainingDummyHostileSkillUseBlockedLocked(
        GameSessionContext attacker,
        DateTimeOffset now)
    {
        var snapshot = CaptureTrainingDummyHostileStatusSnapshotLocked(
            attacker,
            now);
        var controls = snapshot.ActiveStatuses.Aggregate(
            HostileStatusControlFlags.None,
            static (current, status) =>
                current | status.Definition.Control);
        return (controls &
                (HostileStatusControlFlags.NonAttackUsing |
                 HostileStatusControlFlags.NonMagicUsing |
                 HostileStatusControlFlags.NonTechniqueUsing)) != 0;
    }

    private readonly record struct HostileStatusCastTargetPlan(
        GameSessionContext Target,
        PvpEligibilityResult Admission,
        int EffectiveAttackerHit,
        int EffectiveTargetDodge,
        long AttackerVitalsRevision,
        long TargetVitalsRevision);
}
