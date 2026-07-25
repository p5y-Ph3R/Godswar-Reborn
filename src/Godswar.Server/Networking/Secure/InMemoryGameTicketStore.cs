using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class InMemoryGameTicketStore : IGameTicketStore
{
    public const int DefaultCapacity = 1_024;
    public static readonly TimeSpan DefaultTicketTtl =
        TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MinimumTicketTtl =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumTicketTtl =
        TimeSpan.FromMinutes(5);

    private readonly Dictionary<int, GenerationRecord> _generations = [];
    private readonly Guid _authorityId =
        SecureTicketModelValidation.CreateNonzeroId();
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<int, Guid> _grantByAccount = [];
    private readonly Dictionary<Guid, TicketRecord> _tickets = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ticketTtl;
    private bool _disposed;

    public InMemoryGameTicketStore(
        int capacity = DefaultCapacity,
        TimeSpan? ticketTtl = null,
        TimeProvider? timeProvider = null)
    {
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "Ticket capacity must be between 1 and 100,000.");
        }

        var effectiveTtl = ticketTtl ?? DefaultTicketTtl;
        if (effectiveTtl < MinimumTicketTtl ||
            effectiveTtl > MaximumTicketTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticketTtl),
                "Ticket TTL must be between one second and five minutes.");
        }

        _capacity = capacity;
        _ticketTtl = effectiveTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SecureLoginGenerationResult BeginLogin(
        int accountId,
        string username)
    {
        SecureTicketModelValidation.ValidateAccount(accountId, username);
        lock (_gate)
        {
            ThrowIfDisposed();
            CleanupExpired(_timeProvider.GetTimestamp());
            RemoveGeneration(accountId);
            if (_generations.Count >= _capacity)
            {
                return new SecureLoginGenerationResult(
                    SecureLoginGenerationStatus.CapacityExceeded,
                    null);
            }

            var generationId =
                SecureTicketModelValidation.CreateNonzeroId();
            _generations.Add(
                accountId,
                new GenerationRecord(generationId, username));
            return new SecureLoginGenerationResult(
                SecureLoginGenerationStatus.Started,
                new SecureLoginGeneration(
                    _authorityId,
                    generationId,
                    accountId,
                    username));
        }
    }

    public SecureTicketIssueResult Issue(
        SecureLoginGeneration generation,
        SecureConnectionContext loginConnection,
        SecureGameTarget target)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(loginConnection);
        ArgumentNullException.ThrowIfNull(target);
        if (loginConnection.Role != SecureEndpointRole.Login)
        {
            throw new ArgumentException(
                "A ticket can be issued only from a secure login connection.",
                nameof(loginConnection));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var nowTimestamp = _timeProvider.GetTimestamp();
            CleanupExpired(nowTimestamp);
            if (!TryValidateGeneration(generation, out var current))
            {
                return new SecureTicketIssueResult(
                    SecureTicketIssueStatus.GenerationRejected,
                    null);
            }

            RemoveOutstandingTicket(
                generation.AccountId,
                removeGeneration: false);
            if (_tickets.Count >= _capacity)
            {
                return new SecureTicketIssueResult(
                    SecureTicketIssueStatus.CapacityExceeded,
                    null);
            }

            return IssueCore(
                generation,
                current,
                loginConnection,
                target,
                nowTimestamp);
        }
    }

    public SecureTicketConsumeResult Consume(
        SecureGameBind bind,
        SecureConnectionContext gameConnection,
        SecureGameTarget expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(gameConnection);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (gameConnection.Role != SecureEndpointRole.Game)
        {
            throw new ArgumentException(
                "A ticket can be consumed only on a secure game connection.",
                nameof(gameConnection));
        }

        Span<byte> grantIdBytes =
            stackalloc byte[SecureProtocolConstants.GrantIdBytes];
        Span<byte> ticketBytes =
            stackalloc byte[SecureProtocolConstants.TicketBytes];
        Span<byte> suppliedHash = stackalloc byte[32];
        try
        {
            if (!bind.TryCopySecrets(grantIdBytes, ticketBytes))
            {
                return Rejected(SecureTicketConsumeStatus.Rejected);
            }
            SHA256.HashData(ticketBytes, suppliedHash);
            var grantId = new Guid(grantIdBytes);

            lock (_gate)
            {
                ThrowIfDisposed();
                var nowTimestamp = _timeProvider.GetTimestamp();
                if (!_tickets.Remove(grantId, out var record))
                {
                    return Rejected(SecureTicketConsumeStatus.Rejected);
                }

                RemoveGrantIndex(record.AccountId, grantId);
                var expired = IsExpired(record, nowTimestamp);
                var generationCurrent =
                    _generations.TryGetValue(
                        record.AccountId,
                        out var generation) &&
                    generation.GenerationId == record.GenerationId;
                if (generationCurrent)
                {
                    _generations.Remove(record.AccountId);
                }

                var ticketMatches =
                    CryptographicOperations.FixedTimeEquals(
                        suppliedHash,
                        record.TicketHash);
                var scopeMatches =
                    ScopeMatches(
                        record,
                        gameConnection,
                        expectedTarget);
                var accepted =
                    record.Committed &&
                    !expired &&
                    generationCurrent &&
                    ticketMatches &&
                    scopeMatches;
                var principal = accepted
                    ? new SecureBoundGamePrincipal(
                        record.AccountId,
                        record.Username,
                        record.Permissions,
                        record.GenerationId)
                    : null;
                CryptographicOperations.ZeroMemory(record.TicketHash);

                if (accepted)
                {
                    return new SecureTicketConsumeResult(
                        SecureTicketConsumeStatus.Accepted,
                        principal);
                }

                return Rejected(
                    expired
                        ? SecureTicketConsumeStatus.Expired
                        : !scopeMatches
                            ? SecureTicketConsumeStatus.ScopeRejected
                            : SecureTicketConsumeStatus.Rejected);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantIdBytes);
            CryptographicOperations.ZeroMemory(ticketBytes);
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

    public void RevokeGeneration(SecureLoginGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
        {
            if (_disposed ||
                !TryValidateGeneration(generation, out _))
            {
                return;
            }

            RemoveGeneration(generation.AccountId);
        }
    }

    public SecureGameTicketStoreSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CleanupExpired(_timeProvider.GetTimestamp());
            return new SecureGameTicketStoreSnapshot(
                _capacity,
                _generations.Count,
                _tickets.Count);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var record in _tickets.Values)
            {
                CryptographicOperations.ZeroMemory(record.TicketHash);
            }

            _tickets.Clear();
            _grantByAccount.Clear();
            _generations.Clear();
            _disposed = true;
        }
    }

    internal bool TryCommit(Guid generationId, Guid grantId)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_tickets.TryGetValue(grantId, out var record) ||
                record.GenerationId != generationId)
            {
                return false;
            }
            if (IsExpired(record, _timeProvider.GetTimestamp()))
            {
                RemoveTicket(grantId, record, removeGeneration: true);
                return false;
            }

            record.Committed = true;
            return true;
        }
    }

    internal void RevokeGrant(Guid generationId, Guid grantId)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_tickets.TryGetValue(grantId, out var record) ||
                record.GenerationId != generationId)
            {
                return;
            }

            RemoveTicket(grantId, record, removeGeneration: false);
        }
    }

    private SecureTicketIssueResult IssueCore(
        SecureLoginGeneration generation,
        GenerationRecord generationRecord,
        SecureConnectionContext connection,
        SecureGameTarget target,
        long issuedTimestamp)
    {
        Span<byte> grantIdBytes =
            stackalloc byte[SecureProtocolConstants.GrantIdBytes];
        Span<byte> ticketBytes =
            stackalloc byte[SecureProtocolConstants.TicketBytes];
        Span<byte> ticketHash = stackalloc byte[32];
        SecureGameGrant? grant = null;
        TicketRecord? record = null;
        byte[]? storedHash = null;
        var recordAdded = false;
        try
        {
            var grantId = Guid.Empty;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                RandomNumberGenerator.Fill(grantIdBytes);
                grantId = new Guid(grantIdBytes);
                if (grantId != Guid.Empty &&
                    !_tickets.ContainsKey(grantId))
                {
                    break;
                }
            }
            if (grantId == Guid.Empty ||
                _tickets.ContainsKey(grantId))
            {
                throw new CryptographicException(
                    "CSPRNG could not produce a unique nonzero grant ID.");
            }

            var ticketGenerated = false;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                RandomNumberGenerator.Fill(ticketBytes);
                if (!SecureProtocolValidation.IsAllZero(ticketBytes))
                {
                    ticketGenerated = true;
                    break;
                }
            }
            if (!ticketGenerated)
            {
                throw new CryptographicException(
                    "CSPRNG returned repeated invalid ticket secrets.");
            }
            SHA256.HashData(ticketBytes, ticketHash);

            var expiresAt = _timeProvider.GetUtcNow() + _ticketTtl;
            var expiresAtUnixMilliseconds =
                checked((ulong)expiresAt.ToUnixTimeMilliseconds());
            grant = new SecureGameGrant(
                target.RouteHost,
                target.TlsHost,
                target.Audience,
                target.RoutePort,
                target.TlsPort,
                target.ServerId,
                expiresAtUnixMilliseconds,
                grantIdBytes,
                ticketBytes);
            storedHash = ticketHash.ToArray();
            record = new TicketRecord(
                generation.AccountId,
                generationRecord.Username,
                generation.GenerationId,
                connection,
                target,
                issuedTimestamp,
                storedHash);
            _tickets.Add(grantId, record);
            recordAdded = true;
            _grantByAccount[generation.AccountId] = grantId;

            var lease = new SecureGameGrantLease(
                this,
                generation.GenerationId,
                grantId,
                grant);
            return new SecureTicketIssueResult(
                SecureTicketIssueStatus.Issued,
                lease);
        }
        catch
        {
            grant?.Dispose();
            if (recordAdded && record is not null)
            {
                RemoveTicket(
                    new Guid(grantIdBytes),
                    record,
                    removeGeneration: false);
            }
            else if (storedHash is not null)
            {
                CryptographicOperations.ZeroMemory(storedHash);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantIdBytes);
            CryptographicOperations.ZeroMemory(ticketBytes);
            CryptographicOperations.ZeroMemory(ticketHash);
        }
    }

    private bool TryValidateGeneration(
        SecureLoginGeneration generation,
        out GenerationRecord record)
    {
        if (generation.AuthorityId != _authorityId ||
            !_generations.TryGetValue(generation.AccountId, out record!) ||
            record.GenerationId != generation.GenerationId ||
            !string.Equals(
                record.Username,
                generation.Username,
                StringComparison.Ordinal))
        {
            record = null!;
            return false;
        }

        return true;
    }

    private static bool ScopeMatches(
        TicketRecord record,
        SecureConnectionContext connection,
        SecureGameTarget target)
    {
        return record.ProtocolMajor == connection.ProtocolMajor &&
            record.ProtocolMinor == connection.ProtocolMinor &&
            CryptographicOperations.FixedTimeEquals(
                record.ClientInstanceId,
                connection.ClientInstanceId.Span) &&
            CryptographicOperations.FixedTimeEquals(
                record.OriginSha256,
                connection.OriginSha256.Span) &&
            record.ServerId == target.ServerId &&
            record.RoutePort == target.RoutePort &&
            record.TlsPort == target.TlsPort &&
            record.Permissions == target.Permissions &&
            string.Equals(
                record.RouteHost,
                target.RouteHost,
                StringComparison.Ordinal) &&
            string.Equals(
                record.TlsHost,
                target.TlsHost,
                StringComparison.Ordinal) &&
            string.Equals(
                record.Audience,
                target.Audience,
                StringComparison.Ordinal);
    }

    private void CleanupExpired(long nowTimestamp)
    {
        foreach (var entry in _tickets.ToArray())
        {
            if (IsExpired(entry.Value, nowTimestamp))
            {
                RemoveTicket(
                    entry.Key,
                    entry.Value,
                    removeGeneration: true);
            }
        }
    }

    private bool IsExpired(TicketRecord record, long nowTimestamp)
    {
        return _timeProvider.GetElapsedTime(
            record.IssuedTimestamp,
            nowTimestamp) >= _ticketTtl;
    }

    private void RemoveGeneration(int accountId)
    {
        RemoveOutstandingTicket(accountId, removeGeneration: false);
        _generations.Remove(accountId);
    }

    private void RemoveOutstandingTicket(
        int accountId,
        bool removeGeneration)
    {
        if (!_grantByAccount.TryGetValue(accountId, out var grantId) ||
            !_tickets.TryGetValue(grantId, out var record))
        {
            _grantByAccount.Remove(accountId);
            return;
        }

        RemoveTicket(grantId, record, removeGeneration);
    }

    private void RemoveTicket(
        Guid grantId,
        TicketRecord record,
        bool removeGeneration)
    {
        _tickets.Remove(grantId);
        RemoveGrantIndex(record.AccountId, grantId);
        CryptographicOperations.ZeroMemory(record.TicketHash);
        if (removeGeneration &&
            _generations.TryGetValue(record.AccountId, out var generation) &&
            generation.GenerationId == record.GenerationId)
        {
            _generations.Remove(record.AccountId);
        }
    }

    private void RemoveGrantIndex(int accountId, Guid grantId)
    {
        if (_grantByAccount.TryGetValue(accountId, out var current) &&
            current == grantId)
        {
            _grantByAccount.Remove(accountId);
        }
    }

    private static SecureTicketConsumeResult Rejected(
        SecureTicketConsumeStatus status)
    {
        return new SecureTicketConsumeResult(status, null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

}
