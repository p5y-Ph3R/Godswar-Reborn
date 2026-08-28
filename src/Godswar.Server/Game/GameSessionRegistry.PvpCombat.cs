using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Task<PvpBasicAttackDecision> ResolvePvpBasicAttackAsync(
        ClientSession attackingSession,
        uint targetObjectId,
        float reportedAttackerX,
        float reportedAttackerZ,
        long admittedCombatRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ResolvePvpBasicAttackAsync(
            attackingSession,
            targetObjectId,
            reportedAttackerX,
            reportedAttackerZ,
            () => admittedCombatRevision,
            now,
            cancellationToken);

    internal async Task<PvpBasicAttackDecision> ResolvePvpBasicAttackAsync(
        ClientSession attackingSession,
        uint targetObjectId,
        float reportedAttackerX,
        float reportedAttackerZ,
        Func<long> nextAdmittedCombatRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Func<Task?>? admittedAttemptBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(nextAdmittedCombatRevision);
        if (!TryGetCurrentWorldSessionByObjectId(
                attackingSession,
                ResolveCurrentMap(attackingSession),
                targetObjectId,
                out var targetSnapshot))
        {
            return PvpBasicAttackDecision.Reject(
                PvpBasicAttackRejectionReason.TargetUnavailable);
        }

        TryGetRuntimeIncomingDamageMitigation(
            attackingSession,
            now,
            out var attackerRuntime);
        TryGetRuntimeIncomingDamageMitigation(
            targetSnapshot.Session,
            now,
            out var targetMitigation);

        var deathInterruptionSessions = new List<ClientSession>();
        var deathLifeRevisions = new Dictionary<ClientSession, long>();
        PvpBasicAttackDecision decision;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(attackingSession, out var attacker) ||
                !attacker.WorldReady ||
                !_sessions.TryGetValue(targetSnapshot.Session, out var target) ||
                !target.WorldReady ||
                attacker.WorldInstanceId != target.WorldInstanceId ||
                target.WorldRevision != targetSnapshot.WorldRevision ||
                target.ObjectId != targetObjectId ||
                !ReferenceEquals(target.Character, targetSnapshot.Character))
            {
                return PvpBasicAttackDecision.Reject(
                    PvpBasicAttackRejectionReason.StaleWorldOwnership);
            }

            var eligibility = EvaluatePvpBasicAttack(
                attacker.Character,
                target.Character,
                now);
            if (!eligibility.Allowed)
            {
                return PvpBasicAttackDecision.Reject(
                    PvpBasicAttackRejectionReason.AdmissionDenied,
                    eligibility);
            }

            if (!MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                    attacker.Character.PositionX,
                    attacker.Character.PositionZ,
                    reportedAttackerX,
                    reportedAttackerZ,
                    out var attackX,
                    out var attackZ))
            {
                return PvpBasicAttackDecision.Reject(
                    PvpBasicAttackRejectionReason.InvalidPosition,
                    eligibility);
            }

            var sourceStats = CharacterStats.FromCharacter(
                attacker.Character);
            var attackRange = PlayerCombatRules.ResolveBasicAttackRange(
                sourceStats.BasicAttackRange);
            if (eligibility.EntitlementKind ==
                    PvpEntitlementKind.TrainingDummy &&
                eligibility.Admits(
                    attacker.CharacterId,
                    target.CharacterId,
                    attacker.MapId))
            {
                // Player-backed dummies retain a native player collision
                // stand-off. Add the same bounded allowance used by selected
                // hostile skills, but only after exact-dummy entitlement has
                // been established. Ordinary PvP keeps authored weapon reach.
                attackRange += SkillCombatResolver.TargetCollisionAllowance;
            }
            if (!MonsterCombatResolver.IsWithinBasicAttackRange(
                    attackX,
                    attackZ,
                    target.Character.PositionX,
                    target.Character.PositionZ,
                    attackRange))
            {
                return PvpBasicAttackDecision.Reject(
                    PvpBasicAttackRejectionReason.OutOfRange,
                    eligibility);
            }

            if (GetPlayerSkillCastControl(attacker.Session, now) ==
                PlayerSkillCastControl.Stunned)
            {
                return PvpBasicAttackDecision.Reject(
                    PvpBasicAttackRejectionReason.ElementalControl,
                    eligibility);
            }

            var candidates = BuildPvpElementalCandidatesLocked(
                attacker,
                target,
                now);
            var participants = candidates
                .Select(static value => value.Context)
                .Append(attacker)
                .Append(target)
                .DistinctBy(static value => value.CharacterId)
                .ToArray();
            if (!HaveEstablishedPlayerLifeAuthoritiesLocked(
                    participants))
            {
                return PvpBasicAttackDecision.Reject(
                    PvpBasicAttackRejectionReason.AdmissionDenied,
                    eligibility);
            }
            using (AcquirePvpVitalsLocks(participants))
            {
                eligibility = EvaluatePvpBasicAttack(
                    attacker.Character,
                    target.Character,
                    now);
                if (!eligibility.Allowed)
                {
                    return PvpBasicAttackDecision.Reject(
                        PvpBasicAttackRejectionReason.AdmissionDenied,
                        eligibility);
                }

                var targetStats = CharacterStats.FromCharacter(
                    target.Character);
                var targetCombat =
                    CombatCharacterStatsAdapter.ApplyRuntimeTargetModifiers(
                        CombatCharacterStatsAdapter.ToTarget(
                            target.Character.Level,
                            targetStats,
                            targetMitigation.PhysicalDefenseBonus,
                            targetMitigation.MagicDefenseBonus,
                            ToCombatBasisPoints(
                                targetMitigation.PhysicalDamageReduction),
                            ToCombatBasisPoints(
                                targetMitigation.MagicDamageReduction),
                            targetMitigation.
                                PhysicalDamageTakenIncreaseBasisPoints,
                            targetMitigation.
                                MagicDamageTakenIncreaseBasisPoints),
                        targetMitigation.StatusAggregate);
                var attackerCombat =
                    CombatCharacterStatsAdapter.ApplyRuntimeAttackerModifiers(
                        CombatCharacterStatsAdapter.FromCharacter(
                            attacker.Character),
                        attackerRuntime.StatusAggregate);
                if (TryAdjustPvpElementalResolutionInputsLocked(
                        attacker,
                        target,
                        now,
                        attackerCombat,
                        targetCombat,
                        out var elementalInputs))
                {
                    if (!elementalInputs.AttackerActionAllowed)
                    {
                        return PvpBasicAttackDecision.Reject(
                            PvpBasicAttackRejectionReason.ElementalControl,
                            eligibility);
                    }

                    attackerCombat = elementalInputs.Attacker;
                    targetCombat = elementalInputs.Target;
                }

                var admittedCombatRevision =
                    nextAdmittedCombatRevision();
                if (admittedCombatRevision <= 0)
                {
                    throw new InvalidOperationException(
                        "Admitted PvP combat revisions must be positive.");
                }

                var eventId = CombatEventIdentity.ForPlayerBasicAttack(
                    attacker.Character.Id,
                    target.Character.Id,
                    attacker.Character.VitalsRevision,
                    target.Character.VitalsRevision,
                    admittedCombatRevision);
                var resolution = PlayerCombatRules.ResolvePvpBasicAttack(
                    attackerCombat,
                    targetCombat,
                    eventId);
                var attemptEvent = new DeterministicCombatEventContext(
                    eventId,
                    attacker.MapId,
                    attacker.CharacterId,
                    target.CharacterId,
                    now.ToUnixTimeMilliseconds(),
                    CombatEventProvenance.DirectBasicAttack,
                    Committed: false,
                    IsPvp: true,
                    eligibility);
                decision = ResolveCommittedPvpHitLocked(
                    attacker,
                    target,
                    eligibility,
                    resolution,
                    attemptEvent,
                    targetCombat,
                    candidates,
                    now);
                foreach (var victim in decision.KilledPlayers
                             .DistinctBy(static value => value.CharacterId))
                {
                    deathInterruptionSessions.Add(victim.Session);
                    deathLifeRevisions[victim.Session] =
                        AdvancePlayerLifeRevision(
                            victim.Session,
                            now);
                }
            }
        }

        foreach (var changed in decision.ChangedVitals
                     .DistinctBy(static value => value.CharacterId))
        {
            UpdateCharacter(
                changed.Session,
                changed.Character,
                advanceWorldRevision: false);
        }

        // The transaction above already mutated authoritative player vitals.
        // Admit its durable checkpoint and clear death-owned runtime state
        // before any cast or socket await can observe caller cancellation.
        await PersistPvpVitalsAsync(
            decision,
            CancellationToken.None);
        var preparedDeathStatusClears =
            await PreparePvpDeathStatusClearsAsync(
                decision,
                deathLifeRevisions,
                now);

        var distinctDeathSessions = deathInterruptionSessions
            .Distinct()
            .ToArray();
        var interruptions = distinctDeathSessions
            .Select(session => RequestSkillCastInterruptionAsync(
                session,
                SkillCastInterruptionReason.Death,
                CancellationToken.None))
            .ToList();
        var deathSessions = distinctDeathSessions.ToHashSet();
        var shockSessions = decision.ElementalControlCommits
            .Select(static commit => commit.Target.Session)
            .Concat(
                decision.Target is { } committedTarget &&
                decision.ElementalApplications.Any(application =>
                    application.Effect == ElementalEffectKind.Shock &&
                    application.TargetCharacterId ==
                        committedTarget.CharacterId)
                    ? [committedTarget.Session]
                    : [])
            .Where(session => !deathSessions.Contains(session))
            .Distinct();
        interruptions.AddRange(shockSessions.Select(session =>
            RequestSkillCastInterruptionAsync(
                session,
                SkillCastInterruptionReason.Stunned,
                CancellationToken.None)));
        if (decision.Accepted &&
            admittedAttemptBarrier?.Invoke() is { } attackInterruption)
        {
            interruptions.Add(attackInterruption);
        }

        await Task.WhenAll(interruptions);
        await PublishPvpBasicAttackAsync(
            decision,
            now,
            cancellationToken);
        await PublishPreparedPvpDeathStatusClearsAsync(
            preparedDeathStatusClears,
            now,
            cancellationToken);
        return decision;
    }

    private byte ResolveCurrentMap(ClientSession session) =>
        _sessions.TryGetValue(session, out var context)
            ? context.MapId
            : byte.MaxValue;

    private static int ToCombatBasisPoints(decimal value) =>
        decimal.ToInt32(decimal.Round(
            Math.Clamp(value, 0m, 1m) * 10_000m,
            0,
            MidpointRounding.AwayFromZero));
}
