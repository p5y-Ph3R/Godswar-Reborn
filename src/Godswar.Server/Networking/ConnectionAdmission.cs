using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Godswar.Server.Networking;

internal sealed class ConnectionAdmission : IConnectionAdmission
{
    private readonly object _gate = new();
    private readonly Dictionary<IpAddressKey, int> _unauthenticatedByAddress = [];
    private readonly Dictionary<NetworkPrefixKey, int> _unauthenticatedByPrefix = [];
    private readonly ConnectionAdmissionOptions _options;
    private int _activeConnections;
    private int _unauthenticatedConnections;
    private int _loginActiveConnections;
    private int _loginUnauthenticatedConnections;
    private int _gameActiveConnections;
    private int _gameUnauthenticatedConnections;

    public ConnectionAdmission(ConnectionAdmissionOptions options)
    {
        options.Validate();
        _options = options;
    }

    public bool TryAcquire(
        NetworkEndpointRole role,
        IPAddress? remoteAddress,
        [NotNullWhen(true)] out ConnectionAdmissionLease? lease,
        out ConnectionAdmissionRejection rejection)
    {
        lease = null;

        if (!IsValidRole(role))
        {
            rejection = ConnectionAdmissionRejection.InvalidEndpointRole;
            return false;
        }

        if (!IpAddressKey.TryCreate(remoteAddress, out var normalizedAddress, out var addressKey))
        {
            rejection = ConnectionAdmissionRejection.InvalidRemoteAddress;
            return false;
        }

        var prefixKey = NetworkPrefixKey.FromAddress(addressKey);

        lock (_gate)
        {
            if (_activeConnections >= _options.MaxActiveConnections)
            {
                rejection = ConnectionAdmissionRejection.ActiveLimit;
                return false;
            }

            if (_unauthenticatedConnections >= _options.MaxUnauthenticatedConnections)
            {
                rejection = ConnectionAdmissionRejection.UnauthenticatedLimit;
                return false;
            }

            var addressCount = GetCount(_unauthenticatedByAddress, addressKey);
            if (addressCount >= _options.MaxUnauthenticatedConnectionsPerIp)
            {
                rejection = ConnectionAdmissionRejection.PerIpLimit;
                return false;
            }

            var prefixCount = GetCount(_unauthenticatedByPrefix, prefixKey);
            if (prefixCount >= _options.MaxUnauthenticatedConnectionsPerPrefix)
            {
                rejection = ConnectionAdmissionRejection.PrefixLimit;
                return false;
            }

            _activeConnections++;
            _unauthenticatedConnections++;
            _unauthenticatedByAddress[addressKey] = addressCount + 1;
            _unauthenticatedByPrefix[prefixKey] = prefixCount + 1;
            IncrementRole(role);

            lease = new ConnectionAdmissionLease(
                this,
                role,
                normalizedAddress,
                addressKey,
                prefixKey);
            rejection = ConnectionAdmissionRejection.None;
            return true;
        }
    }

    public ConnectionAdmissionSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ConnectionAdmissionSnapshot(
                _activeConnections,
                _unauthenticatedConnections,
                _loginActiveConnections,
                _loginUnauthenticatedConnections,
                _gameActiveConnections,
                _gameUnauthenticatedConnections,
                _unauthenticatedByAddress.Count,
                _unauthenticatedByPrefix.Count);
        }
    }

    internal void MarkAuthenticated(ConnectionAdmissionLease lease)
    {
        lock (_gate)
        {
            if (lease.State != ConnectionAdmissionLease.LeaseState.Unauthenticated)
            {
                return;
            }

            ReleaseUnauthenticatedCounts(lease);
            DecrementRoleUnauthenticated(lease.Role);
            lease.State = ConnectionAdmissionLease.LeaseState.Authenticated;
        }
    }

    internal void Release(ConnectionAdmissionLease lease)
    {
        lock (_gate)
        {
            var state = lease.State;
            if (state == ConnectionAdmissionLease.LeaseState.Released)
            {
                return;
            }

            if (state == ConnectionAdmissionLease.LeaseState.Unauthenticated)
            {
                ReleaseUnauthenticatedCounts(lease);
                DecrementRoleUnauthenticated(lease.Role);
            }

            _activeConnections--;
            DecrementRoleActive(lease.Role);
            lease.State = ConnectionAdmissionLease.LeaseState.Released;
        }
    }

    private static bool IsValidRole(NetworkEndpointRole role)
    {
        return role is NetworkEndpointRole.Login or NetworkEndpointRole.Game;
    }

    private static int GetCount<TKey>(Dictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        return counts.TryGetValue(key, out var count) ? count : 0;
    }

    private static void DecrementOrRemove<TKey>(Dictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        var remaining = counts[key] - 1;
        if (remaining == 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = remaining;
        }
    }

    private void ReleaseUnauthenticatedCounts(ConnectionAdmissionLease lease)
    {
        _unauthenticatedConnections--;
        DecrementOrRemove(_unauthenticatedByAddress, lease.AddressKey);
        DecrementOrRemove(_unauthenticatedByPrefix, lease.PrefixKey);
    }

    private void IncrementRole(NetworkEndpointRole role)
    {
        switch (role)
        {
            case NetworkEndpointRole.Login:
                _loginActiveConnections++;
                _loginUnauthenticatedConnections++;
                break;
            case NetworkEndpointRole.Game:
                _gameActiveConnections++;
                _gameUnauthenticatedConnections++;
                break;
        }
    }

    private void DecrementRoleActive(NetworkEndpointRole role)
    {
        if (role == NetworkEndpointRole.Login)
        {
            _loginActiveConnections--;
        }
        else
        {
            _gameActiveConnections--;
        }
    }

    private void DecrementRoleUnauthenticated(NetworkEndpointRole role)
    {
        if (role == NetworkEndpointRole.Login)
        {
            _loginUnauthenticatedConnections--;
        }
        else
        {
            _gameUnauthenticatedConnections--;
        }
    }
}
