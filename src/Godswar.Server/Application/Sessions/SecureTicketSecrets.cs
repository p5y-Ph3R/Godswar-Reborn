using System.Security.Cryptography;

namespace Godswar.Server.Application.Sessions;

/// <summary>
/// Disposable grant material owned by the authenticated-session boundary.
/// Transport codecs may serialize it, but storage providers never own the
/// wire protocol.
/// </summary>
internal sealed class SecureGameGrant : IDisposable
{
    private readonly object _secretLock = new();
    private readonly byte[] _grantId;
    private readonly byte[] _ticket;
    private bool _disposed;

    public SecureGameGrant(
        string routeHost,
        string tlsHost,
        string audience,
        ushort routePort,
        ushort tlsPort,
        uint targetServerId,
        ulong expiryUnixMilliseconds,
        ReadOnlySpan<byte> grantId,
        ReadOnlySpan<byte> ticket)
    {
        if (!SecureTicketModelValidation.IsDnsName(routeHost, 23))
        {
            throw new ArgumentException(
                "Route host is not a strict ASCII DNS A-label name.",
                nameof(routeHost));
        }
        if (!SecureTicketModelValidation.IsDnsName(tlsHost, 253))
        {
            throw new ArgumentException(
                "TLS host is not a strict ASCII DNS A-label name.",
                nameof(tlsHost));
        }
        if (!SecureTicketModelValidation.IsAudience(audience))
        {
            throw new ArgumentException(
                "Audience is not a valid 1..64 byte token.",
                nameof(audience));
        }
        if (routePort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(routePort));
        }
        if (tlsPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tlsPort));
        }
        if (targetServerId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetServerId));
        }
        ValidateSecret(
            grantId,
            SecureTicketModelValidation.GrantIdBytes,
            nameof(grantId),
            "Game-grant ID");
        ValidateSecret(
            ticket,
            SecureTicketModelValidation.TicketBytes,
            nameof(ticket),
            "Game ticket");

        RouteHost = routeHost;
        TlsHost = tlsHost;
        Audience = audience;
        RoutePort = routePort;
        TlsPort = tlsPort;
        TargetServerId = targetServerId;
        ExpiryUnixMilliseconds = expiryUnixMilliseconds;
        _grantId = grantId.ToArray();
        _ticket = ticket.ToArray();
    }

    public string RouteHost { get; }

    public string TlsHost { get; }

    public string Audience { get; }

    public ushort RoutePort { get; }

    public ushort TlsPort { get; }

    public uint TargetServerId { get; }

    public ulong ExpiryUnixMilliseconds { get; }

    public bool IsDisposed
    {
        get
        {
            lock (_secretLock)
            {
                return _disposed;
            }
        }
    }

    public bool TryCopySecrets(
        Span<byte> grantIdDestination,
        Span<byte> ticketDestination)
    {
        if (grantIdDestination.Length <
                SecureTicketModelValidation.GrantIdBytes ||
            ticketDestination.Length <
                SecureTicketModelValidation.TicketBytes)
        {
            return false;
        }

        lock (_secretLock)
        {
            if (_disposed)
            {
                return false;
            }

            _grantId.CopyTo(
                grantIdDestination[
                    ..SecureTicketModelValidation.GrantIdBytes]);
            _ticket.CopyTo(
                ticketDestination[
                    ..SecureTicketModelValidation.TicketBytes]);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_secretLock)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_grantId);
            CryptographicOperations.ZeroMemory(_ticket);
            _disposed = true;
        }
    }

    private static void ValidateSecret(
        ReadOnlySpan<byte> value,
        int expectedLength,
        string parameterName,
        string description)
    {
        if (value.Length != expectedLength)
        {
            throw new ArgumentException(
                $"{description} must be exactly {expectedLength} bytes.",
                parameterName);
        }
        if (SecureTicketModelValidation.IsAllZero(value))
        {
            throw new ArgumentException(
                $"{description} must be nonzero.",
                parameterName);
        }
    }
}

/// <summary>
/// Disposable proof presented when a TLS game connection redeems a grant.
/// </summary>
internal sealed class SecureGameBind : IDisposable
{
    private readonly object _secretLock = new();
    private readonly byte[] _grantId;
    private readonly byte[] _ticket;
    private bool _disposed;

    public SecureGameBind(
        ReadOnlySpan<byte> grantId,
        ReadOnlySpan<byte> ticket)
    {
        ValidateSecret(
            grantId,
            SecureTicketModelValidation.GrantIdBytes,
            nameof(grantId),
            "Game-grant ID");
        ValidateSecret(
            ticket,
            SecureTicketModelValidation.TicketBytes,
            nameof(ticket),
            "Game ticket");

        _grantId = grantId.ToArray();
        _ticket = ticket.ToArray();
    }

    public bool IsDisposed
    {
        get
        {
            lock (_secretLock)
            {
                return _disposed;
            }
        }
    }

    public bool TryCopySecrets(
        Span<byte> grantIdDestination,
        Span<byte> ticketDestination)
    {
        if (grantIdDestination.Length <
                SecureTicketModelValidation.GrantIdBytes ||
            ticketDestination.Length <
                SecureTicketModelValidation.TicketBytes)
        {
            return false;
        }

        lock (_secretLock)
        {
            if (_disposed)
            {
                return false;
            }

            _grantId.CopyTo(
                grantIdDestination[
                    ..SecureTicketModelValidation.GrantIdBytes]);
            _ticket.CopyTo(
                ticketDestination[
                    ..SecureTicketModelValidation.TicketBytes]);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_secretLock)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_grantId);
            CryptographicOperations.ZeroMemory(_ticket);
            _disposed = true;
        }
    }

    private static void ValidateSecret(
        ReadOnlySpan<byte> value,
        int expectedLength,
        string parameterName,
        string description)
    {
        if (value.Length != expectedLength)
        {
            throw new ArgumentException(
                $"{description} must be exactly {expectedLength} bytes.",
                parameterName);
        }
        if (SecureTicketModelValidation.IsAllZero(value))
        {
            throw new ArgumentException(
                $"{description} must be nonzero.",
                parameterName);
        }
    }
}
