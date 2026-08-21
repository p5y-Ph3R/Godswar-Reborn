using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Reborn-authored LocalDevelopment adapter for authoritative,
    /// instantaneous hostile scalar damage skills.
    /// It does not relax the native hostile-player skill wire gate.
    /// </summary>
    internal async Task<TrainingDummySkillDecision>
        ResolveTrainingDummyDamageScalarAsync(
            ClientSession attackingSession,
            uint casterObjectId,
            uint targetObjectId,
            ReadOnlyMemory<byte> clientSkillCastPacket,
            SkillCombatDefinition authoredSkill,
            Func<long> nextAdmittedCombatRevision,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attackingSession);
        ArgumentNullException.ThrowIfNull(nextAdmittedCombatRevision);
        if (!TrainingDummyDamageSkillPolicy.IsAuthoritativeScalar(
                _gameplayCatalogs,
                authoredSkill))
        {
            return TrainingDummySkillDecision.Reject(
                TrainingDummySkillRejectionReason.UnsupportedSkill);
        }
        if (casterObjectId != LocalPlayerObjectId)
        {
            return TrainingDummySkillDecision.Reject(
                TrainingDummySkillRejectionReason.InvalidCasterObject);
        }
        var animation = TrainingDummySkillAnimationProjection.Create(
            clientSkillCastPacket,
            casterObjectId,
            targetObjectId,
            checked((uint)authoredSkill.SkillId),
            selfArea: false);
        if (!TryGetCurrentWorldSessionByObjectId(
                attackingSession,
                ResolveCurrentMap(attackingSession),
                targetObjectId,
                out var targetSnapshot))
        {
            return TrainingDummySkillDecision.Reject(
                TrainingDummySkillRejectionReason.TargetUnavailable);
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
        PvpBasicAttackDecision combat;
        var currentMana = 0;
        var readyAt = now;
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
                return TrainingDummySkillDecision.Reject(
                    TrainingDummySkillRejectionReason.StaleWorldOwnership);
            }

            lock (attacker.Character.VitalsSync)
            {
                currentMana = attacker.Character.CurrentMp;
            }
            if (_trainingDummies.TryGetCoreIdentity(
                    attacker.Character,
                    out _))
            {
                return TrainingDummySkillDecision.Reject(
                    TrainingDummySkillRejectionReason.AttackerIsTrainingDummy,
                    currentMana);
            }
            var skillRejection = TrainingDummyDamageSkillPolicy.ValidateScalar(
                _gameplayCatalogs,
                authoredSkill,
                attacker.Character.Profession);
            if (skillRejection != TrainingDummySkillRejectionReason.None)
            {
                return TrainingDummySkillDecision.Reject(
                    skillRejection,
                    currentMana);
            }
            var skill = ZodiacOffensiveSkillProjection.Resolve(
                attacker.Character,
                authoredSkill).Skill;
            if (!_trainingDummies.Contains(target.Character))
            {
                return TrainingDummySkillDecision.Reject(
                    TrainingDummySkillRejectionReason.
                        TargetIsNotExactTrainingDummy,
                    currentMana);
            }

            var eligibility = EvaluatePvpBasicAttack(
                attacker.Character,
                target.Character,
                now);
            if (!IsExactTrainingAdmission(eligibility))
            {
                return TrainingDummySkillDecision.Reject(
                    TrainingDummySkillRejectionReason.AdmissionDenied,
                    currentMana,
                    eligibility: eligibility);
            }

            var skillSnapshot = TrainingDummyDamageSkillPolicy.Snapshot(
                skill);
            if (!PlayerCombatRules.IsWithinSkillRange(
                    attacker.Character.PositionX,
                    attacker.Character.PositionZ,
                    target.Character.PositionX,
                    target.Character.PositionZ,
                    skillSnapshot))
            {
                return TrainingDummySkillDecision.Reject(
                    TrainingDummySkillRejectionReason.OutOfRange,
                    currentMana,
                    eligibility: eligibility);
            }
            if (GetPlayerSkillCastControl(attacker.Session, now) ==
                PlayerSkillCastControl.Stunned)
            {
                return TrainingDummySkillDecision.Reject(
                    TrainingDummySkillRejectionReason.ElementalControl,
                    currentMana,
                    eligibility: eligibility);
            }

            // No area or chained candidate is admitted by this adapter.
            IReadOnlyList<PvpElementalCandidate> candidates = [];
            using (AcquirePvpVitalsLocks([attacker, target]))
            {
                eligibility = EvaluatePvpBasicAttack(
                    attacker.Character,
                    target.Character,
                    now);
                currentMana = attacker.Character.CurrentMp;
                if (!IsExactTrainingAdmission(eligibility) ||
                    !_trainingDummies.Contains(target.Character))
                {
                    return TrainingDummySkillDecision.Reject(
                        TrainingDummySkillRejectionReason.AdmissionDenied,
                        currentMana,
                        eligibility: eligibility);
                }
                if (currentMana < skill.Mp)
                {
                    return TrainingDummySkillDecision.Reject(
                        TrainingDummySkillRejectionReason.InsufficientMana,
                        currentMana,
                        eligibility: eligibility);
                }
                var targetStats = target.Character.CalculatedStats ??
                    CharacterStats.FromCharacter(target.Character);
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
                        return TrainingDummySkillDecision.Reject(
                            TrainingDummySkillRejectionReason.
                                ElementalControl,
                            currentMana,
                            eligibility: eligibility);
                    }
                    attackerCombat = elementalInputs.Attacker;
                    targetCombat = elementalInputs.Target;
                }
                if (!TryResolveCurrentHostileSkillOwnerLocked(
                        attackingSession,
                        attacker.Character,
                        out var cooldownOwner))
                {
                    return TrainingDummySkillDecision.Reject(
                        TrainingDummySkillRejectionReason.
                            StaleWorldOwnership,
                        currentMana,
                        eligibility: eligibility);
                }
                if (!_hostileSkillCooldowns.TryClaim(
                        cooldownOwner,
                        checked((uint)skill.SkillId),
                        skill.Cooldown,
                        now,
                        out var cooldownLease,
                        out readyAt))
                {
                    return TrainingDummySkillDecision.Reject(
                        TrainingDummySkillRejectionReason.CooldownActive,
                        currentMana,
                        readyAt,
                        eligibility);
                }

                var commitStarted = false;
                try
                {
                    combat = ResolveTrainingDamageScalarLocked(
                        attacker,
                        target,
                        skillSnapshot,
                        eligibility,
                        attackerCombat,
                        targetCombat,
                        candidates,
                        nextAdmittedCombatRevision,
                        () => commitStarted = true,
                        now);
                    currentMana = attacker.Character.CurrentMp;
                    combat = combat with
                    {
                        ChangedVitals = combat.ChangedVitals
                            .Append(attacker)
                            .DistinctBy(static value => value.CharacterId)
                            .ToArray()
                    };
                }
                catch
                {
                    // Elemental commit state is not reversible. Release the
                    // action lease only while resolution is still pure; once
                    // MP/commit begins, retain both MP and cooldown ownership.
                    if (!commitStarted)
                    {
                        _hostileSkillCooldowns.TryRelease(cooldownLease);
                    }
                    throw;
                }

                foreach (var victim in combat.KilledPlayers
                             .DistinctBy(static value => value.CharacterId))
                {
                    deathInterruptionSessions.Add(victim.Session);
                    deathLifeRevisions[victim.Session] =
                        AdvancePlayerLifeRevision(victim.Session, now);
                }
            }
        }

        foreach (var changed in combat.ChangedVitals
                     .DistinctBy(static value => value.CharacterId))
        {
            UpdateCharacter(
                changed.Session,
                changed.Character,
                advanceWorldRevision: false);
        }

        await PersistPvpVitalsAsync(combat, CancellationToken.None);
        var preparedDeathStatusClears =
            await PreparePvpDeathStatusClearsAsync(
                combat,
                deathLifeRevisions,
                now);
        await InterruptTrainingSkillVictimsAsync(
            [combat],
            deathInterruptionSessions);
        await PublishPvpBasicAttackAsync(
            combat,
            now,
            cancellationToken,
            animation);
        await PublishPreparedPvpDeathStatusClearsAsync(
            preparedDeathStatusClears,
            now,
            cancellationToken);
        return new(
            TrainingDummySkillRejectionReason.None,
            combat,
            currentMana,
            readyAt);
    }

    private PvpBasicAttackDecision ResolveTrainingDamageScalarLocked(
        GameSessionContext attacker,
        GameSessionContext target,
        in PlayerCombatSkillSnapshot skill,
        PvpEligibilityResult eligibility,
        in CombatAttackerStats attackerCombat,
        in CombatTargetStats targetCombat,
        IReadOnlyList<PvpElementalCandidate> candidates,
        Func<long> nextAdmittedCombatRevision,
        Action onCommitStarted,
        DateTimeOffset now)
    {
        var revision = nextAdmittedCombatRevision();
        if (revision <= 0)
        {
            throw new InvalidOperationException(
                "Admitted training-skill revisions must be positive.");
        }
        var eventId = CombatEventIdentity.ForPlayerSkill(
            attacker.CharacterId,
            target.CharacterId,
            attacker.Character.VitalsRevision,
            target.Character.VitalsRevision,
            revision,
            skill.SkillId);
        var resolution = ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
            target.Character,
            attackerCombat,
            targetCombat,
            skill,
            eventId);
        var attempt = new DeterministicCombatEventContext(
            eventId,
            attacker.MapId,
            attacker.CharacterId,
            target.CharacterId,
            now.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectSkill,
            Committed: false,
            IsPvp: true,
            eligibility);
        onCommitStarted();
        attacker.Character.CurrentMp = checked(
            attacker.Character.CurrentMp -
            skill.ManaCost);
        attacker.Character.MarkVitalsChanged();
        return ResolveCommittedPvpHitLocked(
            attacker,
            target,
            eligibility,
            resolution,
            attempt,
            targetCombat,
            candidates,
            now);
    }

    private static bool IsExactTrainingAdmission(
        in PvpEligibilityResult eligibility) =>
        eligibility.Allowed &&
        eligibility.EntitlementKind == PvpEntitlementKind.TrainingDummy;
}
