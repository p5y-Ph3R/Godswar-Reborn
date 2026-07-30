using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Atomically moves an active session between map-owned worlds while
    /// keeping it hidden until the destination snapshot has been delivered.
    /// </summary>
    public bool TryTransferMap(
        ClientSession session,
        byte expectedSourceMapId,
        byte targetMapId,
        float targetX,
        float targetZ)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (targetMapId == expectedSourceMapId ||
            !MapTraversalCatalog.Default.TryGetMap(
                expectedSourceMapId,
                out _) ||
            !MapTraversalCatalog.Default.TryGetMap(
                targetMapId,
                out _) ||
            !MapTraversalLimits.IsFiniteAndBounded(
                new MapTraversalPosition(targetX, targetZ)))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var existing) ||
                existing.MapId != expectedSourceMapId ||
                existing.Character.CurrentMap != expectedSourceMapId ||
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
                MapId = targetMapId,
                Character = character,
                WorldReady = false,
                WorldRevision = nextWorldRevision
            };

            MapInstance.PlayerTransfer? transfer = null;
            var sourceRemoved = false;
            var characterMutated = false;
            try
            {
                EnsureMapObjectIdAvailable(updated);
                transfer = StageMapTransfer(
                    updated,
                    targetMapId,
                    targetX,
                    targetZ);
                RemoveFromMap(existing);
                sourceRemoved = true;
                character.CurrentMap = targetMapId;
                character.PositionX = targetX;
                character.PositionZ = targetZ;
                characterMutated = true;
                transfer.Commit(() => _sessions[session] = updated);

                Console.WriteLine(
                    $"[world] staged hidden map transfer " +
                    $"map={existing.MapId}->{updated.MapId} " +
                    $"character={updated.DisplayName} object={updated.ObjectId} " +
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

                throw;
            }
            finally
            {
                transfer?.Dispose();
            }
        }
    }
}
