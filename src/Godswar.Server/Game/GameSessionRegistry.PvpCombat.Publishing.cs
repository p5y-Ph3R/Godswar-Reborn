using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishPvpBasicAttackAsync(
        PvpBasicAttackDecision decision,
        CancellationToken cancellationToken)
    {
        var attacker = decision.Attacker;
        var target = decision.Target;
        if (!decision.Accepted || attacker is null || target is null)
        {
            return;
        }
        if (!WorldInstances.TryFind(
                attacker.WorldInstanceId,
                out var runtime))
        {
            return;
        }

        var attackerWorldId = WorldObjectIds.ForPlayer(
            attacker.CharacterId);
        var targetWorldId = WorldObjectIds.ForPlayer(
            target.CharacterId);
        var selector = attacker.Character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        foreach (var recipient in GetWorldInstanceSessions(
                     attacker.WorldInstanceId))
        {
            var attackerId = ReferenceEquals(
                    recipient.Session,
                    attacker.Session)
                ? LocalPlayerObjectId
                : attackerWorldId;
            var targetId = ReferenceEquals(
                    recipient.Session,
                    target.Session)
                ? LocalPlayerObjectId
                : targetWorldId;
            try
            {
                if (!await TrySendWorldInstancePacketAsync(
                    runtime,
                    recipient,
                    PacketBuilder.PhysicalDamage(
                        attackerId,
                        attacker.Character.PositionX,
                        0f,
                        attacker.Character.PositionZ,
                        targetId,
                        decision.Resolution.CapturedDamageValue,
                        selector,
                        (byte)decision.Resolution.Outcome),
                    cancellationToken,
                    "PvpBasicAttack"))
                {
                    continue;
                }

                if (decision.ReboundDamage > 0)
                {
                    var reboundSourceId = ReferenceEquals(
                            recipient.Session,
                            target.Session)
                        ? LocalPlayerObjectId
                        : targetWorldId;
                    var reboundTargetId = ReferenceEquals(
                            recipient.Session,
                            attacker.Session)
                        ? LocalPlayerObjectId
                        : attackerWorldId;
                    var reboundSelector =
                        target.Character.Profession is 2 or 3
                            ? (byte)5
                            : (byte)3;
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PhysicalDamage(
                            reboundSourceId,
                            target.Character.PositionX,
                            0f,
                            target.Character.PositionZ,
                            reboundTargetId,
                            decision.ReboundDamage,
                            reboundSelector,
                            (byte)CombatHitOutcome.Normal),
                        cancellationToken,
                        "PvpStatReboundTerminalDamage");
                }

                foreach (var commit in decision.ElementalDamageCommits)
                {
                    var sourceId = ReferenceEquals(
                            recipient.Session,
                            commit.Source.Session)
                        ? LocalPlayerObjectId
                        : WorldObjectIds.ForPlayer(
                            commit.Source.CharacterId);
                    var committedTargetId = ReferenceEquals(
                            recipient.Session,
                            commit.Target.Session)
                        ? LocalPlayerObjectId
                        : WorldObjectIds.ForPlayer(
                            commit.Target.CharacterId);
                    var committedSelector =
                        commit.Source.Character.Profession is 2 or 3
                            ? (byte)5
                            : (byte)3;
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PhysicalDamage(
                            sourceId,
                            commit.Source.Character.PositionX,
                            0f,
                            commit.Source.Character.PositionZ,
                            committedTargetId,
                            checked((uint)commit.AppliedDamage),
                            committedSelector,
                            (byte)CombatHitOutcome.Normal),
                        cancellationToken,
                        "PvpElementalTerminalDamage");
                }

                foreach (var changed in decision.ChangedVitals
                             .DistinctBy(static value => value.CharacterId))
                {
                    var changedId = ReferenceEquals(
                            recipient.Session,
                            changed.Session)
                        ? LocalPlayerObjectId
                        : WorldObjectIds.ForPlayer(changed.CharacterId);
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PlayerVitalsUpdate(
                            changedId,
                            changed.Character.CurrentHp,
                            changed.Character.CurrentMp),
                        cancellationToken,
                        "PvpCommittedVitals");
                }

                foreach (var victim in decision.KilledPlayers
                             .DistinctBy(static value => value.CharacterId))
                {
                    var victimId = ReferenceEquals(
                            recipient.Session,
                            victim.Session)
                        ? LocalPlayerObjectId
                        : WorldObjectIds.ForPlayer(victim.CharacterId);
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PlayerDeath(
                            victimId,
                            victim.Character.PositionX,
                            0f,
                            victim.Character.PositionZ,
                            victim.MapId),
                        cancellationToken,
                        "PvpCommittedDeath");
                }
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(recipient.Session);
            }
        }
    }

    private async Task PersistPvpVitalsAsync(
        PvpBasicAttackDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.Accepted || decision.ChangedVitals.Count == 0)
        {
            return;
        }

        foreach (var changed in decision.ChangedVitals
                     .DistinctBy(static value => value.CharacterId))
        {
            try
            {
                await PersistRoutineVitalsAsync(
                    changed,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[pvp] vitals persistence deferred " +
                    $"character={changed.DisplayName} " +
                    $"attacker={decision.Attacker?.DisplayName ?? "unknown"} " +
                    $"target={decision.Target?.DisplayName ?? "unknown"}: " +
                    ex.Message);
            }
        }
    }

    private async Task<IReadOnlyList<PreparedPvpDeathStatusClear>>
        PreparePvpDeathStatusClearsAsync(
        PvpBasicAttackDecision decision,
        IReadOnlyDictionary<ClientSession, long> lifeRevisions,
        DateTimeOffset now)
    {
        _ = now;
        var prepared = new List<PreparedPvpDeathStatusClear>();
        foreach (var victim in decision.KilledPlayers
                     .DistinctBy(static value => value.CharacterId))
        {
            if (!lifeRevisions.TryGetValue(
                    victim.Session,
                    out var lifeRevision))
            {
                continue;
            }

            if (!_playerStatusStates.TryGetValue(
                    victim.Session,
                    out var state))
            {
                continue;
            }

            await state.Gate.WaitAsync(CancellationToken.None);
            try
            {
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(
                            victim.Session,
                            out var current) ||
                        current.CharacterId != victim.CharacterId ||
                        current.Ownership != victim.Ownership ||
                        !_playerLifeRevisions.TryGetValue(
                            victim.Session,
                            out var currentLifeRevision) ||
                        currentLifeRevision != lifeRevision ||
                        !state.RuntimeStatuses.Remove(
                            MountCatalog.RuntimeStatusKind))
                    {
                        continue;
                    }
                }

                RefreshSkillCastControlSnapshot(state);
                prepared.Add(new(victim, state, lifeRevision));
            }
            finally
            {
                state.Gate.Release();
            }
        }

        return prepared.AsReadOnly();
    }

    private async Task PublishPreparedPvpDeathStatusClearsAsync(
        IReadOnlyList<PreparedPvpDeathStatusClear> prepared,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var clear in prepared)
        {
            await clear.State.Gate.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(
                            clear.Victim.Session,
                            out var current) ||
                        current.CharacterId != clear.Victim.CharacterId ||
                        current.Ownership != clear.Victim.Ownership ||
                        !_playerLifeRevisions.TryGetValue(
                            clear.Victim.Session,
                            out var currentLifeRevision) ||
                        currentLifeRevision != clear.LifeRevision)
                    {
                        continue;
                    }
                }

                await PublishStatusSnapshotLockedAsync(
                    clear.Victim.Session,
                    clear.State,
                    now,
                    "mount-pvp-death",
                    force: true,
                    broadcast: true,
                    cancellationToken,
                    forceLocalGameDataSynchronization: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[mount] failed publishing Ride clear after PvP death " +
                    $"character={clear.Victim.DisplayName}: {ex.Message}");
            }
            finally
            {
                clear.State.Gate.Release();
            }
        }
    }

    private async Task TryClearPvpDeathStatusAsync(
        GameSessionContext victim,
        long lifeRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
                victim.Session,
                lifeRevision,
                MountCatalog.RuntimeStatusKind,
                now,
                "mount-pvp-death",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[mount] failed clearing Ride after PvP death " +
                $"character={victim.DisplayName}: {ex.Message}");
        }
    }

    private readonly record struct PreparedPvpDeathStatusClear(
        GameSessionContext Victim,
        PlayerStatusState State,
        long LifeRevision);
}
