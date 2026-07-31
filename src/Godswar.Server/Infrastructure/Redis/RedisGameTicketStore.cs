using Godswar.Server.Application.Sessions;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisGameTicketStore :
    IGameTicketStore,
    IGameTicketStoreSnapshotSource,
    ISecureGameGrantLeaseAuthority
{
    public const int DefaultCapacity = 1_024;
    public static readonly TimeSpan DefaultTicketTtl =
        TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MinimumTicketTtl =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumTicketTtl =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiredResultRetention =
        TimeSpan.FromSeconds(5);

    private readonly Guid _authorityId =
        SecureTicketModelValidation.CreateNonzeroId();
    private readonly int _capacity;
    private readonly RedisCoordinationExecutor _executor;
    private readonly RedisCoordinationKeyBuilder _keys;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ticketTtl;
    private int _activeGenerations;
    private int _disposed;
    private int _outstandingTickets;

    public RedisGameTicketStore(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        int capacity = DefaultCapacity,
        TimeSpan? ticketTtl = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(keys);
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

        _executor = executor;
        _keys = keys;
        _capacity = capacity;
        _ticketTtl = effectiveTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SecureGameTicketStoreSnapshot GetCachedSnapshot()
    {
        ThrowIfDisposed();
        return new SecureGameTicketStoreSnapshot(
            _capacity,
            Volatile.Read(ref _activeGenerations),
            Volatile.Read(ref _outstandingTickets));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private long RetentionMilliseconds() =>
        checked((long)(
            _ticketTtl + ExpiredResultRetention).TotalMilliseconds);

    private long TicketTtlMilliseconds() =>
        checked((long)_ticketTtl.TotalMilliseconds);

    private void UpdateSnapshot(TicketCounts counts)
    {
        var active = checked((int)Math.Clamp(
            counts.ActiveGenerations,
            0,
            _capacity));
        var outstanding = checked((int)Math.Clamp(
            counts.OutstandingTickets,
            0,
            _capacity));
        Volatile.Write(ref _activeGenerations, active);
        Volatile.Write(ref _outstandingTickets, outstanding);
    }

    private void UpdateOutstanding(long outstanding)
    {
        var bounded = checked((int)Math.Clamp(
            outstanding,
            0,
            _capacity));
        Volatile.Write(ref _outstandingTickets, bounded);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private static void ValidateOperation(
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        deadline.Validate();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
