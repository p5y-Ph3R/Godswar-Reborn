using System.Net;

namespace Godswar.Server.Networking;

internal sealed class ConnectionAdmissionLease : IDisposable
{
    private readonly ConnectionAdmission _owner;
    private readonly IpAddressKey _addressKey;
    private readonly NetworkPrefixKey _prefixKey;
    private int _state;

    internal ConnectionAdmissionLease(
        ConnectionAdmission owner,
        NetworkEndpointRole role,
        IPAddress remoteAddress,
        IpAddressKey addressKey,
        NetworkPrefixKey prefixKey)
    {
        _owner = owner;
        Role = role;
        RemoteAddress = remoteAddress;
        _addressKey = addressKey;
        _prefixKey = prefixKey;
    }

    public NetworkEndpointRole Role { get; }

    public IPAddress RemoteAddress { get; }

    public bool IsAuthenticated => Volatile.Read(ref _state) == (int)LeaseState.Authenticated;

    public bool IsReleased => Volatile.Read(ref _state) == (int)LeaseState.Released;

    /// <summary>
    /// Releases this connection from unauthenticated limits while retaining its
    /// active-connection reservation. Repeated calls are harmless.
    /// </summary>
    public void MarkAuthenticated()
    {
        _owner.MarkAuthenticated(this);
    }

    public void Dispose()
    {
        _owner.Release(this);
    }

    internal IpAddressKey AddressKey => _addressKey;

    internal NetworkPrefixKey PrefixKey => _prefixKey;

    internal LeaseState State
    {
        get => (LeaseState)_state;
        set => Volatile.Write(ref _state, (int)value);
    }

    internal enum LeaseState : byte
    {
        Unauthenticated = 0,
        Authenticated = 1,
        Released = 2,
    }
}
