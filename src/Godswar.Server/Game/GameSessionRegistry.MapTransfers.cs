using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Legacy portal transfer. A byte target always resolves to Tempest's
    /// default open-world instance, never every instance sharing that map.
    /// </summary>
    public bool TryTransferMap(
        ClientSession session,
        byte expectedSourceMapId,
        byte targetMapId,
        float targetX,
        float targetZ)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (targetMapId is 200 or 204 ||
            targetMapId == expectedSourceMapId ||
            !_gameplayCatalogs.MapTraversal.TryGetMap(
                expectedSourceMapId,
                out _) ||
            !_gameplayCatalogs.MapTraversal.TryGetMap(
                targetMapId,
                out _) ||
            !MapTraversalLimits.IsFiniteAndBounded(
                new MapTraversalPosition(targetX, targetZ)))
        {
            return false;
        }

        var target = GetOrCreateDefaultWorldInstance(
            targetMapId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                    session,
                    out var existing) ||
                existing.MapId != expectedSourceMapId)
            {
                return false;
            }

            return TryTransferWorldInstanceCore(
                session,
                existing,
                target,
                targetX,
                targetZ);
        }
    }

    internal bool TryTransferWorldInstance(
        ClientSession session,
        WorldInstanceId expectedSourceInstanceId,
        WorldInstanceId targetInstanceId,
        float targetX,
        float targetZ)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (expectedSourceInstanceId == targetInstanceId ||
            !MapTraversalLimits.IsFiniteAndBounded(
                new MapTraversalPosition(targetX, targetZ)) ||
            !WorldInstances.TryFind(
                targetInstanceId,
                out var target))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                    session,
                    out var existing) ||
                existing.WorldInstanceId !=
                    expectedSourceInstanceId)
            {
                return false;
            }

            return TryTransferWorldInstanceCore(
                session,
                existing,
                target,
                targetX,
                targetZ);
        }
    }

    private bool TryTransferWorldInstanceCore(
        ClientSession session,
        GameSessionContext existing,
        WorldInstanceRuntime target,
        float targetX,
        float targetZ)
    {
        if (target.Descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Active ||
            !MayEnterWorldInstance(
                target,
                existing.CharacterId) ||
            existing.Character.CurrentMap != existing.MapId ||
            !existing.WorldReady ||
            existing.Ownership.IsValid &&
            !IsCurrentAccountSession(
                existing.AccountId,
                session,
                existing.Ownership))
        {
            return false;
        }

        var character = existing.Character;
        var nextWorldRevision =
            checked(existing.WorldRevision + 1);
        var oldMapId = character.CurrentMap;
        var oldX = character.PositionX;
        var oldZ = character.PositionZ;
        var updated = existing with
        {
            RealmId = target.RealmId,
            WorldInstanceId = target.InstanceId,
            MapId = target.MapId,
            Character = character,
            WorldReady = false,
            WorldRevision = nextWorldRevision,
            WorldMembershipEpoch = NextWorldMembershipEpochLocked()
        };

        EnsureMapObjectIdAvailable(updated);
        var placementChange =
            PrepareWorldPlacement(existing, updated);
        WorldInstancePlayerTransfer? transfer = null;
        var sourceRemoved = false;
        var characterMutated = false;
        try
        {
            transfer = StageMapTransfer(
                updated,
                target.MapId,
                targetX,
                targetZ);
            RemoveFromMap(existing);
            sourceRemoved = true;

            character.CurrentMap = target.MapId;
            character.PositionX = targetX;
            character.PositionZ = targetZ;
            characterMutated = true;
            transfer.Commit(
                () => _sessions[session] = updated);

            Console.WriteLine(
                $"[world] staged hidden instance transfer " +
                $"instance={existing.WorldInstanceId}" +
                $"->{updated.WorldInstanceId} " +
                $"map={existing.MapId}->{updated.MapId} " +
                $"character={updated.DisplayName} " +
                $"object={updated.ObjectId} " +
                $"account={updated.AccountId}");
            return true;
        }
        catch
        {
            if (characterMutated)
            {
                character.CurrentMap = oldMapId;
                character.PositionX = oldX;
                character.PositionZ = oldZ;
            }
            if (sourceRemoved)
            {
                AddToMap(existing);
                _sessions[session] = existing;
            }

            RollBackWorldPlacement(
                placementChange,
                existing,
                updated);
            throw;
        }
        finally
        {
            transfer?.Dispose();
        }
    }
}
