using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly Dictionary<ClientSession, TrainingDummyHostileStatusState>
        _trainingDummyHostileStatuses = [];

    private bool TryCommitTrainingDummyHostileStatusLocked(
        GameSessionContext attacker,
        GameSessionContext target,
        in PvpEligibilityResult admission,
        in HostileStatusEffectDefinition definition,
        in HostileStatusTriggerEvidence evidence,
        int effectiveAttackerHit,
        int effectiveTargetDodge,
        DateTimeOffset now,
        Action<GameSessionContext, HostileStatusEffectDefinition>?
            claimAppliedInterruption,
        out HostileStatusApplicationDecision decision)
    {
        if (!Monitor.IsEntered(_gate))
        {
            throw new SynchronizationLockException(
                "The training-dummy hostile status transaction requires " +
                "the registry gate.");
        }

        definition.Validate();
        if (definition.Trigger != evidence.Trigger ||
            evidence.Trigger ==
                HostileStatusApplicationTrigger.CommittedDamagingHit &&
            evidence.AppliedDamage == 0)
        {
            decision = Reject(
                HostileStatusApplicationDisposition.InvalidTrigger);
            return false;
        }
        if (evidence.EventId == 0 || evidence.TargetOrder < 0)
        {
            decision = Reject(
                HostileStatusApplicationDisposition.InvalidEvent);
            return false;
        }
        if (!IsCurrentHostileStatusParticipantLocked(attacker) ||
            !IsCurrentHostileStatusParticipantLocked(target) ||
            attacker.WorldInstanceId != target.WorldInstanceId)
        {
            decision = Reject(
                HostileStatusApplicationDisposition.StaleWorldOwnership);
            return false;
        }
        if (attacker.Character.Profession != definition.RequiredProfession ||
            _trainingDummies.TryGetCoreIdentity(attacker.Character, out _))
        {
            decision = Reject(
                HostileStatusApplicationDisposition.InvalidAttacker);
            return false;
        }
        if (!_trainingDummies.Contains(target.Character))
        {
            _trainingDummyHostileStatuses.Remove(target.Session);
            decision = Reject(
                HostileStatusApplicationDisposition.
                    TargetIsNotExactTrainingDummy);
            return false;
        }
        if (!IsExactTrainingAdmission(admission) ||
            !admission.Admits(
                attacker.CharacterId,
                target.CharacterId,
                attacker.MapId))
        {
            decision = Reject(
                HostileStatusApplicationDisposition.AdmissionDenied);
            return false;
        }
        if (attacker.Character.CurrentHp <= 0 ||
            target.Character.CurrentHp <= 0)
        {
            ClearTrainingDummyHostileStatusesLocked(target.Session);
            decision = Reject(
                HostileStatusApplicationDisposition.TargetDead);
            return false;
        }

        var state = GetOrReplaceHostileStatusStateLocked(target);
        PruneHostileStatusStateLocked(state, now);
        var eventKey = new HostileStatusEventKey(
            evidence.EventId,
            evidence.TargetOrder,
            definition.SkillId,
            definition.Kind);
        if (state.RecentEvents.ContainsKey(eventKey))
        {
            state.ActiveStatuses.TryGetValue(
                definition.Kind,
                out var replayActive);
            decision = new(
                HostileStatusApplicationDisposition.ReplaySuppressed,
                default,
                replayActive);
            return false;
        }

        RememberHostileStatusEventLocked(
            state,
            eventKey,
            HostileStatusDurationPolicy.ResolveExpiry(
                now,
                definition.Duration));
        var sourceStats = CharacterStats.FromCharacter(
            attacker.Character);
        var targetStats = CharacterStats.FromCharacter(
            target.Character);
        var proc = HostileStatusProcPolicy.Evaluate(
            new HostileStatusProcRatings(
                attacker.Character.Level,
                target.Character.Level,
                effectiveAttackerHit,
                effectiveTargetDodge,
                sourceStats.StatusHit,
                targetStats.StatusResistance),
            evidence.EventId,
            evidence.TargetOrder);
        if (!proc.Applied)
        {
            decision = new(
                HostileStatusApplicationDisposition.ProcMiss,
                proc,
                null);
            return false;
        }
        if (state.ActiveStatuses.TryGetValue(
                definition.Kind,
                out var existing) &&
            existing.ExpiresAt > now &&
            existing.Definition.Priority > definition.Priority)
        {
            decision = new(
                HostileStatusApplicationDisposition.HigherPriorityActive,
                proc,
                existing);
            return false;
        }

        var revision = checked(state.Revision + 1);
        var expiresAt = HostileStatusDurationPolicy.ResolveExpiry(
            now,
            definition.Duration);
        if (claimAppliedInterruption is not null)
        {
            try
            {
                // The sink claims the victim's pending-cast generation
                // synchronously before its first await. Keep that claim
                // before the active-state write so status commit and cast
                // completion have one unambiguous linearization order.
                claimAppliedInterruption(target, definition);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[hostile-status] interruption claim failed before " +
                    $"commit target={target.DisplayName} " +
                    $"skill={definition.SkillId}: {ex.Message}");
            }
        }

        var active = new ActiveTrainingDummyHostileStatus(
            definition,
            now,
            expiresAt,
            evidence.EventId,
            evidence.TargetOrder,
            attacker.CharacterId,
            revision);
        state.Revision = revision;
        state.ActiveStatuses[definition.Kind] = active;
        decision = new(
            HostileStatusApplicationDisposition.Applied,
            proc,
            active);
        return true;
    }

    internal TrainingDummyHostileStatusSnapshot
        CaptureTrainingDummyHostileStatusSnapshot(
            ClientSession session,
            DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var context)
                ? CaptureTrainingDummyHostileStatusSnapshotLocked(
                    context,
                    now)
                : TrainingDummyHostileStatusSnapshot.Empty;
        }
    }

    internal HostileStatusControlFlags GetTrainingDummyHostileControl(
        ClientSession session,
        DateTimeOffset now)
    {
        var snapshot = CaptureTrainingDummyHostileStatusSnapshot(
            session,
            now);
        return snapshot.ActiveStatuses.Aggregate(
            HostileStatusControlFlags.None,
            static (flags, status) => flags | status.Definition.Control);
    }

    private TrainingDummyHostileIncomingModifiers
        CaptureTrainingDummyHostileIncomingModifiersLocked(
            GameSessionContext target,
            DateTimeOffset now)
    {
        var snapshot = CaptureTrainingDummyHostileStatusSnapshotLocked(
            target,
            now);
        return ComposeTrainingDummyHostileIncomingModifiers(snapshot);
    }

    private TrainingDummyHostileIncomingModifiers
        PreviewTrainingDummyHostileIncomingModifiersLocked(
            GameSessionContext target,
            DateTimeOffset now)
    {
        if (!Monitor.IsEntered(_gate))
        {
            throw new SynchronizationLockException(
                "The hostile status preview requires the registry gate.");
        }
        if (!_trainingDummyHostileStatuses.TryGetValue(
                target.Session,
                out var state) ||
            !state.Matches(target) ||
            !_trainingDummies.Contains(target.Character) ||
            target.Character.CurrentHp <= 0)
        {
            return default;
        }

        var snapshot = new TrainingDummyHostileStatusSnapshot(
            target.CharacterId,
            state.Revision,
            state.ActiveStatuses.Values
                .Where(status => status.ExpiresAt > now)
                .OrderBy(static status => status.Definition.StatusId)
                .ToArray());
        return ComposeTrainingDummyHostileIncomingModifiers(snapshot);
    }

    private static TrainingDummyHostileIncomingModifiers
        ComposeTrainingDummyHostileIncomingModifiers(
            in TrainingDummyHostileStatusSnapshot snapshot)
    {
        long physicalDefense = 0;
        long magicDefense = 0;
        long physicalTaken = 0;
        long magicTaken = 0;
        long physicalReduction = 0;
        long magicReduction = 0;
        foreach (var active in snapshot.ActiveStatuses)
        {
            var definition = active.Definition;
            physicalDefense += definition.PhysicalDefenseModifier;
            magicDefense += definition.MagicDefenseModifier;
            physicalTaken +=
                definition.PhysicalDamageTakenIncreaseBasisPoints;
            magicTaken += definition.MagicDamageTakenIncreaseBasisPoints;
            physicalReduction +=
                definition.PhysicalDamageReductionBasisPoints;
            magicReduction +=
                definition.MagicDamageReductionBasisPoints;
        }

        return new(
            ClampInt(physicalDefense),
            ClampInt(magicDefense),
            ClampBasisPoints(physicalTaken),
            ClampBasisPoints(magicTaken),
            ClampBasisPoints(physicalReduction),
            ClampBasisPoints(magicReduction));
    }

    private TrainingDummyHostileStatusSnapshot
        CaptureTrainingDummyHostileStatusSnapshotLocked(
            GameSessionContext target,
            DateTimeOffset now)
    {
        if (!Monitor.IsEntered(_gate))
        {
            throw new SynchronizationLockException(
                "The hostile status snapshot requires the registry gate.");
        }
        if (!_trainingDummyHostileStatuses.TryGetValue(
                target.Session,
                out var state))
        {
            return TrainingDummyHostileStatusSnapshot.Empty;
        }
        if (!state.Matches(target) ||
            !_trainingDummies.Contains(target.Character) ||
            target.Character.CurrentHp <= 0)
        {
            _trainingDummyHostileStatuses.Remove(target.Session);
            return TrainingDummyHostileStatusSnapshot.Empty;
        }

        PruneHostileStatusStateLocked(state, now);
        return new(
            target.CharacterId,
            state.Revision,
            state.ActiveStatuses.Values
                .Where(status => status.ExpiresAt > now)
                .OrderBy(static status => status.Definition.StatusId)
                .ToArray());
    }

    private bool IsCurrentHostileStatusParticipantLocked(
        GameSessionContext participant) =>
        _sessions.TryGetValue(participant.Session, out var current) &&
        current.WorldReady &&
        current.CharacterId == participant.CharacterId &&
        current.WorldRevision == participant.WorldRevision &&
        current.WorldInstanceId == participant.WorldInstanceId &&
        current.ObjectId == participant.ObjectId &&
        current.Ownership == participant.Ownership &&
        ReferenceEquals(current.Character, participant.Character);

    private TrainingDummyHostileStatusState
        GetOrReplaceHostileStatusStateLocked(GameSessionContext target)
    {
        if (_trainingDummyHostileStatuses.TryGetValue(
                target.Session,
                out var state) &&
            state.Matches(target))
        {
            return state;
        }

        state = new TrainingDummyHostileStatusState(target);
        _trainingDummyHostileStatuses[target.Session] = state;
        return state;
    }

    private static void PruneHostileStatusStateLocked(
        TrainingDummyHostileStatusState state,
        DateTimeOffset now)
    {
        foreach (var kind in state.ActiveStatuses
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            state.ActiveStatuses.Remove(kind);
        }
        foreach (var key in state.RecentEvents
                     .Where(pair => pair.Value <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            state.RecentEvents.Remove(key);
        }
    }

    private static void RememberHostileStatusEventLocked(
        TrainingDummyHostileStatusState state,
        HostileStatusEventKey key,
        DateTimeOffset retainUntil)
    {
        if (state.RecentEvents.Count >=
            TrainingDummyHostileStatusState.MaximumRecentEvents)
        {
            var oldest = state.RecentEvents
                .OrderBy(static pair => pair.Value)
                .ThenBy(static pair => pair.Key.EventId)
                .First();
            state.RecentEvents.Remove(oldest.Key);
        }
        state.RecentEvents[key] = retainUntil;
    }

    private void ClearTrainingDummyHostileStatusesLocked(
        ClientSession session)
    {
        if (!Monitor.IsEntered(_gate))
        {
            throw new SynchronizationLockException(
                "Hostile status clear requires the registry gate.");
        }
        _trainingDummyHostileStatuses.Remove(session);
    }

    private static HostileStatusApplicationDecision Reject(
        HostileStatusApplicationDisposition disposition) =>
        new(disposition, default, null);

    private static int ClampInt(long value) =>
        (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private static int ClampBasisPoints(long value) =>
        (int)Math.Clamp(value, 0L, 10_000L);
}
