using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<MountRideActivationCommit?> TryActivateMountRideAndPublishAsync(
        ClientSession session,
        int expectedCharacterId,
        long expectedLifeRevision,
        MountRideDefinition mount,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool castCompletionClaimed = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return null;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            GameCharacter character;
            int accountId;
            int currentMana;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var context) ||
                    !context.WorldReady ||
                    context.CharacterId != expectedCharacterId ||
                    !_playerLifeRevisions.TryGetValue(session, out var lifeRevision) ||
                    (!castCompletionClaimed &&
                     lifeRevision != expectedLifeRevision))
                {
                    return null;
                }

                character = context.Character;
                accountId = context.AccountId;
                lock (character.VitalsSync)
                {
                    if ((!castCompletionClaimed &&
                         character.CurrentHp <= 0) ||
                        character.Level < mount.MountLevel ||
                        !MountCatalog.TryGetEquippedRideDefinition(character, out var currentMount) ||
                        currentMount != mount ||
                        character.CurrentMp < MountCatalog.RideManaCost ||
                        state.RuntimeStatuses.TryGetValue(
                            MountCatalog.RuntimeStatusKind,
                            out var existing) && existing.ExpiresAt > now)
                    {
                        return null;
                    }

                    var statusRevision = checked(state.Revision + 1);
                    var status = new ActiveRuntimeStatus(
                        mount.StatusId,
                        MountCatalog.RuntimeStatusKind,
                        Priority: 1,
                        Beneficial: false,
                        DateTimeOffset.MaxValue,
                        ClientStatusAggregate.Empty,
                        statusRevision,
                        MovementSpeedBonus: mount.SpeedBonus);
                    character.CurrentMp -= MountCatalog.RideManaCost;
                    currentMana = character.CurrentMp;
                    character.MarkVitalsChanged();
                    state.Revision = statusRevision;
                    state.RuntimeStatuses[MountCatalog.RuntimeStatusKind] = status;
                    RefreshSkillCastControlSnapshot(state);
                }
            }

            if (_checkpointCoordinator is not null ||
                _store is not null)
            {
                try
                {
                    // Ride is not externally successful until its MP cost is
                    // durable. Do not let a socket cancellation after status
                    // publication refund the cast on the next login.
                    await FlushVitalsAsync(
                        accountId,
                        character,
                        CancellationToken.None);
                }
                catch
                {
                    state.RuntimeStatuses.Remove(MountCatalog.RuntimeStatusKind);
                    RefreshSkillCastControlSnapshot(state);
                    lock (character.VitalsSync)
                    {
                        character.CurrentMp = Math.Min(
                            character.MaxMp,
                            (int)Math.Min(
                                int.MaxValue,
                                (long)character.CurrentMp + MountCatalog.RideManaCost));
                        character.MarkVitalsChanged();
                    }

                    try
                    {
                        await FlushVitalsAsync(
                            accountId,
                            character,
                            CancellationToken.None);
                    }
                    catch (Exception rollbackError)
                    {
                        Console.WriteLine(
                            $"[mount] Ride MP rollback persistence failed character={character.Name}: {rollbackError.Message}");
                    }

                    throw;
                }
            }

            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                "mount-ride",
                force: true,
                broadcast: true,
                cancellationToken,
                synchronizeLocalMovementSpeed: true);
            return new MountRideActivationCommit(character, currentMana, StatusChanged: true);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
        ClientSession session,
        long expectedLifeRevision,
        int kind,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!_sessions.ContainsKey(session) ||
                    !_playerLifeRevisions.TryGetValue(session, out var lifeRevision) ||
                    lifeRevision != expectedLifeRevision ||
                    !state.RuntimeStatuses.Remove(kind))
                {
                    return false;
                }
            }

            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force: true,
                broadcast: true,
                cancellationToken,
                synchronizeLocalMovementSpeed:
                    kind == MountCatalog.RuntimeStatusKind);
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal ClientStatusAggregate GetRuntimeStatusAggregate(
        ClientSession session,
        DateTimeOffset now)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return ClientStatusAggregate.Empty;
        }

        state.Gate.Wait();
        try
        {
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return EvaluatePlayerStatusEcsLocked(
                                session,
                                state,
                                context,
                                now)
                            .Snapshot
                            .Aggregate;
                    }
                }
            }

            var active = state.RuntimeStatuses.Values
                .Where(status => status.ExpiresAt > now)
                .ToArray();
            return new ClientStatusAggregate(
                active.Sum(static status => status.Modifiers.Hit),
                active.Sum(static status => status.Modifiers.CriticalAppend),
                0f,
                1f + active.Sum(static status => status.MovementSpeedBonus),
                active.Any(static status => status.Kind == MountCatalog.RuntimeStatusKind));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal decimal GetRuntimePhysicalDamageReduction(
        ClientSession session,
        DateTimeOffset now)
    {
        TryGetRuntimePhysicalDamageReduction(
            session,
            now,
            out var reduction);
        return reduction;
    }

    internal bool TryGetRuntimePhysicalDamageReduction(
        ClientSession session,
        DateTimeOffset now,
        out decimal reduction)
    {
        reduction = 0m;
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
            {
                return true;
            }

            lock (_gate)
            {
                return _sessions.ContainsKey(session);
            }
        }

        state.Gate.Wait();
        try
        {
            IEnumerable<ActiveRuntimeStatus> statuses;
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                RuntimeStatusSessionLookupHook?.Invoke();
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return false;
                    }

                    statuses = EvaluatePlayerStatusEcsLocked(
                            session,
                            state,
                            context,
                            now)
                        .ActiveRuntimeStatuses;
                }
            }
            else
            {
                statuses = state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now)
                    .ToArray();
            }

            reduction = Math.Clamp(
                statuses.Sum(
                    static status =>
                        status.PhysicalDamageReduction),
                0m,
                1m);
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task SendStatusSnapshotToViewerAsync(
        GameSessionContext player,
        ClientSession viewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(viewer);

        var snapshot = await GetStatusSnapshotAsync(
            player.Session,
            DateTimeOffset.UtcNow,
            cancellationToken);

        await viewer.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                player.Character,
                player.ObjectId,
                snapshot.Effects,
                snapshot.Aggregate),
            cancellationToken,
            "VisiblePlayerStatusEffects");
    }

    internal async Task<PlayerStatusSnapshot> GetStatusSnapshotAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return PlayerStatusComposer.Compose(
                ExperienceBoostState.Empty,
                [],
                now);
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return EvaluatePlayerStatusEcsLocked(
                                session,
                                state,
                                context,
                                now)
                            .Snapshot;
                    }
                }
            }

            return PlayerStatusComposer.Compose(
                state.ExperienceBoosts,
                state.RuntimeStatuses.Values,
                now);
        }
        finally
        {
            state.Gate.Release();
        }
    }

}
