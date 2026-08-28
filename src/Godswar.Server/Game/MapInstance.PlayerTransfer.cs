using Godswar.Server.Networking;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal PlayerTransfer StagePlayerTransfer(
        GameSessionContext context,
        PlayerTransformOverride? transformOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteWithMedusaCharacterAdmission(
            context.CharacterId,
            () => StagePlayerTransferCore(
                context,
                transformOverride));
    }

    private PlayerTransfer StagePlayerTransferCore(
        GameSessionContext context,
        PlayerTransformOverride? transformOverride)
    {
        lock (_membershipGate)
        {
            if (_sessions.ContainsKey(context.Session) ||
                _ecsShadow.ContainsPlayer(context.Session))
            {
                throw new InvalidOperationException(
                    $"Session is already present on map {MapId}.");
            }

            lock (_monsterRuntimeGate)
            {
                EnsurePlayerObjectIdDoesNotCollideWithNpcs(context);
            }

            if (!_ecsShadow.TryAddOrUpdatePlayer(
                    context,
                    transformOverride))
            {
                _ecsShadow.ClearPlayerFault(context.Session);
                throw new InvalidOperationException(
                    $"ECS rejected player {context.ObjectId} on map {MapId}.");
            }
        }

        return new PlayerTransfer(this, context);
    }

    private void CommitPlayerTransfer(
        GameSessionContext context,
        Action publishRegistryContext)
    {
        lock (_membershipGate)
        {
            publishRegistryContext();
            _sessions[context.Session] = context;
        }
    }

    private void RollBackPlayerTransfer(
        ClientSession session)
    {
        lock (_membershipGate)
        {
            if (!_sessions.ContainsKey(session))
            {
                _ecsShadow.TryRemovePlayer(session);
            }
        }
    }

    internal sealed class PlayerTransfer : IDisposable
    {
        private MapInstance? _map;
        private readonly GameSessionContext _context;

        internal PlayerTransfer(
            MapInstance map,
            GameSessionContext context)
        {
            _map = map;
            _context = context;
        }

        public void Commit(Action publishRegistryContext)
        {
            ArgumentNullException.ThrowIfNull(publishRegistryContext);
            var map = _map ??
                throw new ObjectDisposedException(nameof(PlayerTransfer));
            map.CommitPlayerTransfer(
                _context,
                publishRegistryContext);
            _map = null;
        }

        public void Dispose()
        {
            var map = Interlocked.Exchange(ref _map, null);
            map?.RollBackPlayerTransfer(_context.Session);
        }
    }
}
