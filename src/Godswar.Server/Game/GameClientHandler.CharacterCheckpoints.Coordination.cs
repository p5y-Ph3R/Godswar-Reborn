using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async ValueTask ReleasePlayerCoordinationLeaseAsync()
    {
        var lease = Interlocked.Exchange(
            ref _playerCoordinationLease,
            null);
        if (lease is null)
        {
            return;
        }

        try
        {
            _ = await lease.ReleaseAsync();
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[coordination] player lease release failed " +
                $"reason={error.GetType().Name}");
        }

        try
        {
            var accountId = _account?.Id ?? _character?.AccountId ?? 0;
            if (accountId > 0)
            {
                _ = await _accountPresence
                    .TryMarkAccountPlayerOfflineAsync(
                        accountId,
                        lease.LeaseToken,
                        CancellationToken.None);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[coordination] account presence release failed " +
                $"reason={error.GetType().Name}");
        }
    }

    private bool TryResolveCoordinatedRoute(
        byte mapId,
        out CoordinatedWorldRoute route,
        bool requireInitialGatewayRoute = false)
    {
        route = default;
        if (_playerCoordination is null ||
            !_playerCoordination.TryResolveRoute(mapId, out route))
        {
            return false;
        }

        var admission = _session.GatewayWorldAdmission;
        if (admission is null)
        {
            return true;
        }
        if (admission.TargetNodeId != _playerCoordination.NodeId)
        {
            return false;
        }

        // The mTLS gateway admission authorizes this worker process. Its
        // initial route must match exactly, but subsequent server-authorized
        // portal transitions may select another route owned by the same
        // worker. Cross-worker handoff remains fail-closed because the local
        // issuer cannot resolve a remote route.
        return !requireInitialGatewayRoute ||
            admission.RealmId == route.RealmId &&
            admission.MapId == route.MapId &&
            admission.WorldInstanceId == route.WorldInstanceId;
    }

    private async ValueTask<bool>
        PublishPlayerCoordinationEnteringAsync(
            byte mapId,
            CancellationToken cancellationToken)
    {
        if (_playerCoordinationLease is null)
        {
            return _playerCoordination?.IsEnabled != true;
        }
        if (!TryResolveCoordinatedRoute(mapId, out var route) ||
            !await _playerCoordinationLease.PublishEnteringAsync(
                route,
                cancellationToken))
        {
            RejectLostPlayerOwnership();
            return false;
        }
        return true;
    }

    private async ValueTask<bool> PublishPlayerCoordinationOnlineAsync(
        byte mapId,
        CancellationToken cancellationToken)
    {
        if (_playerCoordinationLease is null)
        {
            return _playerCoordination?.IsEnabled != true;
        }
        if (!TryResolveCoordinatedRoute(mapId, out var route) ||
            !await _playerCoordinationLease.PublishOnlineAsync(
                route,
                cancellationToken))
        {
            RejectLostPlayerOwnership();
            return false;
        }
        return true;
    }
}
