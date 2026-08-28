using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Handles authoritative, instantaneous self-centred damage definitions
    /// when at least one immutable training-dummy identity is in the authoritative
    /// radius. With no such target, the caller retains the normal PvE path.
    /// Spear Blast 320-324 and Meteor Blast 330-334 commit their sealed rank
    /// Injury only after a nonlethal damaging hit; the triggering hit is never
    /// amplified.
    /// </summary>
    internal async Task<TrainingDummyAreaSkillDecision>
        ResolveTrainingDummyDamageAreaAsync(
            ClientSession attackingSession,
            uint casterObjectId,
            ReadOnlyMemory<byte> clientSkillCastPacket,
            SkillCombatDefinition authoredSkill,
            Func<long> nextAdmittedCombatRevision,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attackingSession);
        ArgumentNullException.ThrowIfNull(nextAdmittedCombatRevision);
        if (!TrainingDummyDamageSkillPolicy.IsAuthoritativeArea(
                _gameplayCatalogs,
                authoredSkill))
        {
            return TrainingDummyAreaSkillDecision.Reject(
                TrainingDummySkillRejectionReason.UnsupportedSkill);
        }
        if (casterObjectId != LocalPlayerObjectId)
        {
            return TrainingDummyAreaSkillDecision.Reject(
                TrainingDummySkillRejectionReason.InvalidCasterObject);
        }
        var animation = TrainingDummySkillAnimationProjection.Create(
            clientSkillCastPacket,
            casterObjectId,
            casterObjectId,
            checked((uint)authoredSkill.SkillId),
            selfArea: true);
        if (!_sessions.TryGetValue(attackingSession, out var route) ||
            !route.WorldReady)
        {
            return TrainingDummyAreaSkillDecision.Reject(
                TrainingDummySkillRejectionReason.StaleWorldOwnership);
        }

        var targetSnapshots = GetWorldInstanceSessions(route.WorldInstanceId)
            .Where(candidate =>
                !ReferenceEquals(candidate.Session, attackingSession) &&
                _trainingDummies.Contains(candidate.Character))
            .OrderBy(static candidate => candidate.ObjectId)
            .ToArray();

        // Status-state gates precede the registry gate in the established
        // lock order. Snapshot every exact candidate before authoritative
        // range selection so movement cannot introduce a lock inversion.
        TryGetRuntimeIncomingDamageMitigation(
            attackingSession,
            now,
            out var attackerRuntime);
        var targetMitigations = new Dictionary<
            ClientSession,
            RuntimeIncomingDamageMitigation>();
        foreach (var snapshot in targetSnapshots)
        {
            TryGetRuntimeIncomingDamageMitigation(
                snapshot.Session,
                now,
                out var mitigation);
            targetMitigations[snapshot.Session] = mitigation;
        }

        var combats = new List<PvpBasicAttackDecision>();
        var deathInterruptionSessions = new List<ClientSession>();
        var deathLifeRevisions = new Dictionary<ClientSession, long>();
        var currentMana = 0;
        var readyAt = now;
        Exception? partialFailure = null;
        GameSessionContext? committedAttacker = null;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(attackingSession, out var attacker) ||
                !attacker.WorldReady ||
                attacker.WorldInstanceId != route.WorldInstanceId ||
                attacker.WorldRevision != route.WorldRevision ||
                !ReferenceEquals(attacker.Character, route.Character))
            {
                return TrainingDummyAreaSkillDecision.Reject(
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
                return TrainingDummyAreaSkillDecision.Reject(
                    TrainingDummySkillRejectionReason.AttackerIsTrainingDummy,
                    currentMana);
            }
            var skillRejection = TrainingDummyDamageSkillPolicy.ValidateArea(
                _gameplayCatalogs,
                authoredSkill,
                attacker.Character.Profession);
            if (skillRejection != TrainingDummySkillRejectionReason.None)
            {
                return TrainingDummyAreaSkillDecision.Reject(
                    skillRejection,
                    currentMana);
            }
            var skill = ZodiacOffensiveSkillProjection.Resolve(
                attacker.Character,
                authoredSkill).Skill;

            var targets = new List<GameSessionContext>();
            foreach (var snapshot in targetSnapshots)
            {
                if (!_sessions.TryGetValue(snapshot.Session, out var current) ||
                    !current.WorldReady ||
                    current.WorldInstanceId != attacker.WorldInstanceId ||
                    current.WorldRevision != snapshot.WorldRevision ||
                    current.ObjectId != snapshot.ObjectId ||
                    !ReferenceEquals(current.Character, snapshot.Character) ||
                    !_trainingDummies.Contains(current.Character))
                {
                    return TrainingDummyAreaSkillDecision.Reject(
                        TrainingDummySkillRejectionReason.StaleWorldOwnership,
                        currentMana);
                }
                if (current.Character.CurrentHp > 0 &&
                    SkillCombatResolver.IsWithinArea(
                        attacker.Character.PositionX,
                        attacker.Character.PositionZ,
                        current.Character.PositionX,
                        current.Character.PositionZ,
                        skill))
                {
                    targets.Add(current);
                }
            }

            targets.Sort(static (left, right) =>
                left.ObjectId.CompareTo(right.ObjectId));
            if (targets.Count == 0)
            {
                return TrainingDummyAreaSkillDecision.NotApplicable();
            }

            var participants = targets.Prepend(attacker).ToArray();
            if (!HaveEstablishedPlayerLifeAuthoritiesLocked(
                    participants))
            {
                return TrainingDummyAreaSkillDecision.Reject(
                    TrainingDummySkillRejectionReason.StaleWorldOwnership,
                    attacker.Character.CurrentMp);
            }
            using (AcquirePvpVitalsLocks(participants))
            {
                if (GetPlayerSkillCastControl(attacker.Session, now) ==
                    PlayerSkillCastControl.Stunned)
                {
                    return TrainingDummyAreaSkillDecision.Reject(
                        TrainingDummySkillRejectionReason.ElementalControl,
                        attacker.Character.CurrentMp);
                }

                currentMana = attacker.Character.CurrentMp;
                if (currentMana < skill.Mp)
                {
                    return TrainingDummyAreaSkillDecision.Reject(
                        TrainingDummySkillRejectionReason.InsufficientMana,
                        currentMana);
                }
                if (!TryResolveCurrentHostileSkillOwnerLocked(
                        attackingSession,
                        attacker.Character,
                        out var cooldownOwner))
                {
                    return TrainingDummyAreaSkillDecision.Reject(
                        TrainingDummySkillRejectionReason.StaleWorldOwnership,
                        currentMana);
                }

                var plans = new List<TrainingDummyAreaTargetPlan>(
                    targets.Count);
                foreach (var target in targets)
                {
                    if (!_trainingDummies.Contains(target.Character) ||
                        target.Character.CurrentHp <= 0 ||
                        !SkillCombatResolver.IsWithinArea(
                            attacker.Character.PositionX,
                            attacker.Character.PositionZ,
                            target.Character.PositionX,
                            target.Character.PositionZ,
                            skill))
                    {
                        return TrainingDummyAreaSkillDecision.Reject(
                            TrainingDummySkillRejectionReason.
                                StaleWorldOwnership,
                            currentMana);
                    }

                    var eligibility = EvaluatePvpBasicAttack(
                        attacker.Character,
                        target.Character,
                        now);
                    if (!IsExactTrainingAdmission(eligibility))
                    {
                        return TrainingDummyAreaSkillDecision.Reject(
                            TrainingDummySkillRejectionReason.AdmissionDenied,
                            currentMana);
                    }

                    var targetStats = CharacterStats.FromCharacter(
                        target.Character);
                    var mitigation = targetMitigations[target.Session];
                    var targetCombat = CombatCharacterStatsAdapter
                        .ApplyRuntimeTargetModifiers(
                            CombatCharacterStatsAdapter.ToTarget(
                                target.Character.Level,
                                targetStats,
                                mitigation.PhysicalDefenseBonus,
                                mitigation.MagicDefenseBonus,
                                ToCombatBasisPoints(
                                    mitigation.PhysicalDamageReduction),
                                ToCombatBasisPoints(
                                    mitigation.MagicDamageReduction),
                                mitigation.
                                    PhysicalDamageTakenIncreaseBasisPoints,
                                mitigation.
                                    MagicDamageTakenIncreaseBasisPoints),
                            mitigation.StatusAggregate);
                    var attackerCombat = CombatCharacterStatsAdapter
                        .ApplyRuntimeAttackerModifiers(
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
                            return TrainingDummyAreaSkillDecision.Reject(
                                TrainingDummySkillRejectionReason.
                                    ElementalControl,
                                currentMana);
                        }
                        attackerCombat = elementalInputs.Attacker;
                        targetCombat = elementalInputs.Target;
                    }

                    plans.Add(new(
                        target,
                        eligibility,
                        attackerCombat,
                        targetCombat,
                        attacker.Character.VitalsRevision,
                        target.Character.VitalsRevision));
                }

                if (!_hostileSkillCooldowns.TryClaim(
                        cooldownOwner,
                        checked((uint)skill.SkillId),
                        skill.Cooldown,
                        now,
                        out var cooldownLease,
                        out readyAt))
                {
                    return TrainingDummyAreaSkillDecision.Reject(
                        TrainingDummySkillRejectionReason.CooldownActive,
                        currentMana,
                        readyAt);
                }

                long actionRevision;
                PvpBasicAttackDecision[] resolved;
                try
                {
                    actionRevision = nextAdmittedCombatRevision();
                    if (actionRevision <= 0)
                    {
                        throw new InvalidOperationException(
                            "Admitted training-area revisions must be positive.");
                    }
                    resolved = plans.Select(plan =>
                        ResolveTrainingAreaBase(
                            attacker,
                            plan,
                            skill,
                            actionRevision,
                            now)).ToArray();
                }
                catch
                {
                    _hostileSkillCooldowns.TryRelease(cooldownLease);
                    throw;
                }

                attacker.Character.CurrentMp = checked(
                    attacker.Character.CurrentMp - skill.Mp);
                attacker.Character.MarkVitalsChanged();
                committedAttacker = attacker;

                // Elemental commit state is not transactionally reversible.
                // All fallible validation is therefore complete before this
                // sorted sequence. An unexpected internal failure retains the
                // action's MP/cooldown and every earlier target subtransaction.
                try
                {
                    for (var index = 0; index < plans.Count; index++)
                    {
                        var plan = plans[index];
                        var decision = CommitTrainingAreaTargetLocked(
                            attacker,
                            plan,
                            resolved[index],
                            skill,
                            now);
                        combats.Add(decision);
                    }
                }
                catch (Exception ex)
                {
                    partialFailure = ex;
                }

                if (combats.Count > 0)
                {
                    var killed = combats
                        .SelectMany(static combat => combat.KilledPlayers)
                        .DistinctBy(static value => value.CharacterId)
                        .ToArray();
                    for (var index = 0; index < combats.Count; index++)
                    {
                        var isLast = index == combats.Count - 1;
                        combats[index] = combats[index] with
                        {
                            ChangedVitals = combats[index].ChangedVitals
                                .Where(value => !ReferenceEquals(
                                    value.Session,
                                    attacker.Session))
                                .Concat(isLast
                                    ? new[] { attacker }
                                    : Array.Empty<GameSessionContext>())
                                .DistinctBy(static value => value.CharacterId)
                                .ToArray(),
                            KilledPlayers = isLast ? killed : []
                        };
                    }
                }

                foreach (var victim in combats
                             .SelectMany(static combat =>
                                 combat.KilledPlayers)
                             .DistinctBy(static value => value.CharacterId))
                {
                    deathInterruptionSessions.Add(victim.Session);
                    deathLifeRevisions[victim.Session] =
                        AdvancePlayerLifeRevision(victim.Session, now);
                }
            }
        }

        var changedVitals = combats
            .SelectMany(static combat => combat.ChangedVitals)
            .Append(committedAttacker!)
            .Where(static context => context is not null)
            .DistinctBy(static context => context.CharacterId)
            .ToArray();
        foreach (var changed in changedVitals)
        {
            UpdateCharacter(
                changed.Session,
                changed.Character,
                advanceWorldRevision: false);
        }

        var preparedDeathStatusClears =
            new List<PreparedPvpDeathStatusClear>();
        foreach (var combat in combats)
        {
            await PersistPvpVitalsAsync(combat, CancellationToken.None);
            preparedDeathStatusClears.AddRange(
                await PreparePvpDeathStatusClearsAsync(
                    combat,
                    deathLifeRevisions,
                    now));
        }
        if (combats.Count == 0 && committedAttacker is not null)
        {
            await PersistRoutineVitalsAsync(
                committedAttacker,
                CancellationToken.None);
        }

        await InterruptTrainingSkillVictimsAsync(
            combats,
            deathInterruptionSessions);
        for (var index = 0; index < combats.Count; index++)
        {
            await PublishPvpBasicAttackAsync(
                combats[index],
                now,
                cancellationToken,
                index == 0 ? animation : null);
        }
        await PublishPreparedPvpDeathStatusClearsAsync(
            preparedDeathStatusClears,
            now,
            cancellationToken);

        if (partialFailure is not null)
        {
            Console.WriteLine(
                "[training-skill] area partial commit " +
                $"skill={authoredSkill.SkillId} committed={combats.Count} " +
                $"error={partialFailure.Message}");
            return new(
                true,
                TrainingDummySkillRejectionReason.PartialCommitFailure,
                combats.AsReadOnly(),
                committedAttacker?.Character.CurrentMp ?? currentMana,
                readyAt);
        }

        return new(
            true,
            TrainingDummySkillRejectionReason.None,
            combats.AsReadOnly(),
            committedAttacker?.Character.CurrentMp ?? currentMana,
            readyAt);
    }

}
