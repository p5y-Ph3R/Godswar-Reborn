using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal void JoinGatewayWorld(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint objectId,
        GatewayWorldAdmission admission,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
    {
        ValidateGatewayWorldJoin(
            accountId,
            character,
            admission);

        JoinWorldInstanceCore(
            session,
            accountId,
            character,
            objectId,
            GetOrCreateGatewayWorldInstance(admission),
            worldReady,
            joinedAt);
    }

    internal uint JoinPlayerGatewayWorld(
        ClientSession session,
        int accountId,
        GameCharacter character,
        GatewayWorldAdmission admission,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
    {
        ValidateGatewayWorldJoin(
            accountId,
            character,
            admission);

        return JoinWorldInstanceCore(
            session,
            accountId,
            character,
            requestedObjectId: null,
            runtime: GetOrCreateGatewayWorldInstance(admission),
            worldReady: worldReady,
            joinedAt: joinedAt);
    }

    internal void JoinWorldInstance(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint objectId,
        WorldInstanceId instanceId,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
    {
        var runtime = GetRequiredWorldInstance(instanceId);
        if (runtime.MapId != character.CurrentMap ||
            runtime.Descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Active)
        {
            throw new InvalidOperationException(
                "The requested world instance cannot accept this " +
                "character.");
        }

        JoinWorldInstanceCore(
            session,
            accountId,
            character,
            objectId,
            runtime,
            worldReady,
            joinedAt);
    }

    private uint JoinWorldInstanceCore(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint? requestedObjectId,
        WorldInstanceRuntime runtime,
        bool worldReady,
        DateTimeOffset? joinedAt)
    {
        if (character.RealmId != runtime.RealmId)
        {
            throw new InvalidOperationException(
                "The character realm does not match the requested " +
                "world instance.");
        }

        var onlineStartedAt =
            joinedAt ?? DateTimeOffset.UtcNow;
        var ownership = PlayerOwnership(character);
        ValidateWorldJoinOwnership(
            session,
            accountId,
            ownership);

        GameSessionContext context;
        GameSessionContext? previous;
        lock (_gate)
        {
            if (session.IsDisconnected)
            {
                throw new InvalidOperationException(
                    "A disconnected session cannot join a world instance.");
            }

            _sessions.TryGetValue(session, out previous);
            var objectId = requestedObjectId ??
                AllocatePlayerObjectIdLocked(
                    session,
                    character.Id,
                    previous);
            context = new GameSessionContext(
                session,
                accountId,
                character.Id,
                character.Name,
                runtime.RealmId,
                runtime.InstanceId,
                character.CurrentMap,
                objectId,
                character,
                worldReady,
                runtime.Descriptor.Revision)
            {
                Ownership = ownership,
                WorldMembershipEpoch =
                    NextWorldMembershipEpochLocked()
            };
            EnsureMapObjectIdAvailable(context);
            var placementChange =
                PrepareWorldPlacement(previous, context);
            var instanceChanged =
                previous is not null &&
                previous.WorldInstanceId !=
                    context.WorldInstanceId;
            WorldInstancePlayerTransfer? transfer = null;
            var sourceRemoved = false;
            try
            {
                if (instanceChanged &&
                    _playerRuntimeMode ==
                        PlayerRuntimeMode.Ecs)
                {
                    transfer = StageMapTransfer(context);
                }

                if (instanceChanged)
                {
                    RemoveFromMap(previous!);
                    sourceRemoved = true;
                }

                if (transfer is not null)
                {
                    transfer.Commit(
                        () => _sessions[session] = context);
                }
                else
                {
                    AddToMap(context);
                    _sessions[session] = context;
                }
            }
            catch
            {
                if (sourceRemoved && previous is not null)
                {
                    AddToMap(previous);
                    _sessions[session] = previous;
                }

                RollBackWorldPlacement(
                    placementChange,
                    previous,
                    context);
                throw;
            }
            finally
            {
                transfer?.Dispose();
            }

            ResetOnlineRuntimeState(
                session,
                accountId,
                character,
                context,
                previous,
                onlineStartedAt,
                worldReady);
        }

        LogWorldJoin(previous, context);
        return context.ObjectId;
    }

    private long NextWorldMembershipEpochLocked()
    {
        var next = checked(_nextWorldMembershipEpoch + 1);
        _nextWorldMembershipEpoch = next;
        return next;
    }

    private static void ValidateGatewayWorldJoin(
        int accountId,
        GameCharacter character,
        GatewayWorldAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.AccountId != accountId ||
            admission.RealmId != character.RealmId ||
            admission.CharacterId != 0 &&
            admission.CharacterId != character.Id ||
            !admission.MapId.TryGetLegacyValue(out var legacyMapId) ||
            legacyMapId != character.CurrentMap)
        {
            throw new InvalidOperationException(
                "The gateway admission does not match the joining " +
                "account and character.");
        }
    }

    private void ValidateWorldJoinOwnership(
        ClientSession session,
        int accountId,
        PlayerOwnershipFence ownership)
    {
        if (_requiresDurablePlayerPersistence &&
            !ownership.IsValid)
        {
            throw new InvalidOperationException(
                "Durable world join requires an acquired player " +
                "ownership fence.");
        }
        if (ownership.IsValid &&
            !IsCurrentAccountSession(
                accountId,
                session,
                ownership))
        {
            throw new InvalidOperationException(
                "The world join does not own the current player fence.");
        }
    }

    private void ResetOnlineRuntimeState(
        ClientSession session,
        int accountId,
        GameCharacter character,
        GameSessionContext context,
        GameSessionContext? previous,
        DateTimeOffset onlineStartedAt,
        bool worldReady)
    {
        if (previous is not null &&
            previous.CharacterId != character.Id)
        {
            _nextPlayerRecoveryAt.TryRemove(
                previous.CharacterId,
                out _);
            RemovePlayerRuntimeEcs(session);
        }

        _playerLifeRevisions.TryAdd(session, 0);
        var recoveryDeadline = _nextPlayerRecoveryAt.GetOrAdd(
            character.Id,
            static _ => new PlayerRecoveryDeadline(
                DateTimeOffset.UnixEpoch));
        recoveryDeadline.Write(
            onlineStartedAt + PlayerRecoveryInterval);
        _zodiacOnlineSessions.AddOrUpdate(
            session,
            _ => new ZodiacOnlineSessionState(
                accountId,
                character.Id,
                character,
                onlineStartedAt),
            (_, existing) =>
            {
                if (existing.CharacterId == character.Id)
                {
                    existing.Character = character;
                    return existing;
                }

                return new ZodiacOnlineSessionState(
                    accountId,
                    character.Id,
                    character,
                    onlineStartedAt);
            });
        if (worldReady)
        {
            StartProgressionBoostOnlineSession(
                session,
                accountId,
                character.Id,
                previous?.CharacterId,
                onlineStartedAt);
        }
    }

    private void LogWorldJoin(
        GameSessionContext? previous,
        GameSessionContext context)
    {
        if (previous is null)
        {
            Console.WriteLine(
                $"[world] joined realm={context.RealmId} " +
                $"instance={context.WorldInstanceId} map={context.MapId} " +
                $"character={context.DisplayName} object={context.ObjectId} " +
                $"account={context.AccountId} population=" +
                $"{GetWorldInstancePopulation(context.WorldInstanceId)}");
        }
        else if (previous.WorldInstanceId !=
                 context.WorldInstanceId)
        {
            Console.WriteLine(
                $"[world] moved instance=" +
                $"{previous.WorldInstanceId}->{context.WorldInstanceId} " +
                $"map={previous.MapId}->{context.MapId} " +
                $"character={context.DisplayName} object={context.ObjectId} " +
                $"account={context.AccountId} population=" +
                $"{GetWorldInstancePopulation(context.WorldInstanceId)}");
        }
    }

    private WorldPlacementChange PrepareWorldPlacement(
        GameSessionContext? previous,
        GameSessionContext next)
    {
        RequireWorldInstanceAdmission(next);
        if (previous is null)
        {
            var assigned = CompletePlacement(
                WorldInstances.AssignCharacterAsync(
                    next.CharacterId,
                    next.WorldInstanceId,
                    CancellationToken.None));
            RequirePlacement(
                assigned,
                WorldInstancePlacementStatus.Assigned,
                "assign character to world instance");
            return new WorldPlacementChange(
                WorldPlacementChangeKind.Assigned);
        }

        if (previous.CharacterId == next.CharacterId)
        {
            if (previous.WorldInstanceId ==
                next.WorldInstanceId)
            {
                return default;
            }

            var transferred = CompletePlacement(
                WorldInstances.TransferCharacterAsync(
                    next.CharacterId,
                    previous.WorldInstanceId,
                    next.WorldInstanceId,
                    CancellationToken.None));
            RequirePlacement(
                transferred,
                WorldInstancePlacementStatus.Transferred,
                "transfer character between world instances");
            return new WorldPlacementChange(
                WorldPlacementChangeKind.Transferred);
        }

        var released = CompletePlacement(
            WorldInstances.ReleaseCharacterAsync(
                previous.CharacterId,
                previous.WorldInstanceId,
                CancellationToken.None));
        RequirePlacement(
            released,
            WorldInstancePlacementStatus.Released,
            "release previous character from world instance");
        try
        {
            var assigned = CompletePlacement(
                WorldInstances.AssignCharacterAsync(
                    next.CharacterId,
                    next.WorldInstanceId,
                    CancellationToken.None));
            RequirePlacement(
                assigned,
                WorldInstancePlacementStatus.Assigned,
                "assign replacement character to world instance");
            return new WorldPlacementChange(
                WorldPlacementChangeKind.Replaced);
        }
        catch
        {
            RestoreWorldPlacement(previous);
            throw;
        }
    }

    private void RollBackWorldPlacement(
        WorldPlacementChange change,
        GameSessionContext? previous,
        GameSessionContext next)
    {
        switch (change.Kind)
        {
            case WorldPlacementChangeKind.None:
                return;
            case WorldPlacementChangeKind.Assigned:
                ReleaseWorldPlacement(next);
                return;
            case WorldPlacementChangeKind.Transferred:
                if (previous is null)
                {
                    throw new InvalidOperationException(
                        "Transferred placement has no source context.");
                }
                RequirePlacement(
                    CompletePlacement(
                        WorldInstances.TransferCharacterAsync(
                            next.CharacterId,
                            next.WorldInstanceId,
                            previous.WorldInstanceId,
                            CancellationToken.None)),
                    WorldInstancePlacementStatus.Transferred,
                    "roll back world-instance transfer");
                return;
            case WorldPlacementChangeKind.Replaced:
                ReleaseWorldPlacement(next);
                RestoreWorldPlacement(previous!);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(change));
        }
    }

    private void ReleaseWorldPlacement(
        GameSessionContext context)
    {
        RequirePlacement(
            CompletePlacement(
                WorldInstances.ReleaseCharacterAsync(
                    context.CharacterId,
                    context.WorldInstanceId,
                    CancellationToken.None)),
            WorldInstancePlacementStatus.Released,
            "release character from world instance");
    }

    private void RestoreWorldPlacement(
        GameSessionContext context)
    {
        RequirePlacement(
            CompletePlacement(
                WorldInstances.AssignCharacterAsync(
                    context.CharacterId,
                    context.WorldInstanceId,
                    CancellationToken.None)),
            WorldInstancePlacementStatus.Assigned,
            "restore character world-instance placement");
    }

    private static WorldInstancePlacementResult CompletePlacement(
        ValueTask<WorldInstancePlacementResult> pending) =>
        pending.IsCompletedSuccessfully
            ? pending.Result
            : pending.AsTask().GetAwaiter().GetResult();

    private static void RequirePlacement(
        WorldInstancePlacementResult result,
        WorldInstancePlacementStatus expected,
        string operation)
    {
        if (result.Status != expected)
        {
            throw new InvalidOperationException(
                $"Cannot {operation}: {result.Status}.");
        }
    }

    private readonly record struct WorldPlacementChange(
        WorldPlacementChangeKind Kind);

    private enum WorldPlacementChangeKind : byte
    {
        None = 0,
        Assigned = 1,
        Transferred = 2,
        Replaced = 3
    }
}
