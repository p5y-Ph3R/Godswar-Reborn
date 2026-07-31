using Godswar.Server.Application.Coordination;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

/// <summary>
/// Owns one process-wide multiplexer and bounds every logical Redis command.
/// Timed-out driver work retains its permit until the underlying task ends.
/// </summary>
internal sealed class RedisCoordinationExecutor :
    IAsyncDisposable
{
    private static readonly CommandMap SinglePrimaryCommandMap =
        CommandMap.Create(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CONFIG",
                "CLUSTER"
            },
            available: false);

    private readonly TimeSpan _circuitOpenDuration;
    private readonly int _failureThreshold;
    private readonly IDatabase _database;
    private readonly SemaphoreSlim _gate;
    private readonly int _maximumConcurrency;
    private readonly ConnectionMultiplexer _multiplexer;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _queueAdmissionTimeout;
    private readonly object _stateGate = new();
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _circuitOpenUntil;
    private DateTimeOffset _lastSuccess;
    private int _consecutiveFailures;
    private int _disposed;
    private int _inFlight;
    private long _accepted;
    private long _circuitOpen;
    private long _conflicts;
    private long _overloads;
    private long _timeouts;
    private long _unavailable;

    private RedisCoordinationExecutor(
        ConnectionMultiplexer multiplexer,
        int database,
        CoordinationRuntimeOptions options,
        TimeProvider timeProvider)
    {
        _multiplexer = multiplexer;
        _database = multiplexer.GetDatabase(database);
        _maximumConcurrency = options.MaximumConcurrentOperations;
        _gate = new SemaphoreSlim(
            _maximumConcurrency,
            _maximumConcurrency);
        _queueAdmissionTimeout = options.QueueAdmissionTimeout;
        _operationTimeout = options.OperationTimeout;
        _failureThreshold = options.CircuitFailureThreshold;
        _circuitOpenDuration = options.CircuitOpenDuration;
        _timeProvider = timeProvider;
    }

    public static async ValueTask<RedisCoordinationExecutor> ConnectAsync(
        CoordinationRuntimeOptions options,
        string clientName,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        if (options.ProviderKind != CoordinationProviderKind.Redis ||
            string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "Validated Redis coordination options are required.");
        }

        var configuration =
            ConfigurationOptions.Parse(options.ConnectionString);
        if (options.RequireTls && !configuration.Ssl)
        {
            throw new InvalidDataException(
                "Redis coordination requires TLS for this profile.");
        }

        configuration.AbortOnConnectFail = true;
        configuration.AllowAdmin = false;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.ClientName = BoundedClientName(clientName);
        configuration.ConnectRetry = 1;
        configuration.ConnectTimeout =
            options.ConnectTimeoutMilliseconds;
        configuration.SyncTimeout =
            options.OperationTimeoutMilliseconds;
        configuration.AsyncTimeout =
            options.OperationTimeoutMilliseconds;
        configuration.KeepAlive = Math.Max(
            1,
            options.ServerHeartbeatSeconds);
        configuration.DefaultDatabase = options.Database;
        configuration.CommandMap = SinglePrimaryCommandMap;
        configuration.TieBreaker = string.Empty;

        var connect = ConnectionMultiplexer.ConnectAsync(configuration);
        ConnectionMultiplexer multiplexer;
        try
        {
            multiplexer = await connect.WaitAsync(
                TimeSpan.FromMilliseconds(
                    options.ConnectTimeoutMilliseconds),
                cancellationToken);
        }
        catch
        {
            if (connect.IsCompletedSuccessfully)
            {
                connect.Result.Dispose();
            }
            else
            {
                _ = DisposeLateConnectionAsync(connect);
            }
            throw;
        }

        return new RedisCoordinationExecutor(
            multiplexer,
            options.Database,
            options,
            timeProvider ?? TimeProvider.System);
    }

    public async ValueTask<T> ExecuteAsync<T>(
        RedisCoordinationOperationFamily family,
        CoordinationDeadline deadline,
        Func<IDatabase, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        var started = RedisCoordinationMetrics.Start();
        ThrowIfCircuitOpen(family, started);
        await EnterAsync(family, deadline, started, cancellationToken);

        Task<T>? pending = null;
        var releaseHere = true;
        try
        {
            pending = operation(_database);
            var timeout = EffectiveTimeout(deadline);
            if (timeout <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            var result = await pending.WaitAsync(
                timeout,
                cancellationToken);
            RecordSuccess(family, started);
            return result;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (pending is { IsCompleted: false })
            {
                releaseHere = false;
                _ = ReleaseWhenCompleteAsync(pending);
            }
            RedisCoordinationMetrics.Record(
                family,
                RedisCoordinationOutcome.Cancelled,
                started);
            throw;
        }
        catch (Exception error)
            when (error is TimeoutException or RedisTimeoutException)
        {
            if (pending is { IsCompleted: false })
            {
                releaseHere = false;
                _ = ReleaseWhenCompleteAsync(pending);
            }
            RecordFailure(
                family,
                RedisCoordinationOutcome.Timeout,
                started);
            Interlocked.Increment(ref _timeouts);
            throw new RedisCoordinationException(
                CoordinationOperationStatus.DeadlineExceeded,
                error);
        }
        catch (Exception error)
            when (error is RedisConnectionException or
                RedisServerException)
        {
            RecordFailure(
                family,
                RedisCoordinationOutcome.Unavailable,
                started);
            Interlocked.Increment(ref _unavailable);
            throw new RedisCoordinationException(
                CoordinationOperationStatus.Unavailable,
                error);
        }
        finally
        {
            if (releaseHere)
            {
                ReleasePermit();
            }
        }
    }

    public void RecordLogicalOutcome(
        RedisCoordinationOperationFamily family,
        CoordinationOperationStatus status)
    {
        switch (status)
        {
            case CoordinationOperationStatus.Conflict:
                Interlocked.Increment(ref _conflicts);
                break;
            case CoordinationOperationStatus.NotFound:
                break;
        }
        RedisCoordinationMetrics.RecordLogical(family, status);
    }

    public RedisCoordinationExecutorSnapshot GetSnapshot()
    {
        lock (_stateGate)
        {
            return new RedisCoordinationExecutorSnapshot(
                IsReady:
                    Volatile.Read(ref _disposed) == 0 &&
                    _multiplexer.IsConnected &&
                    _timeProvider.GetUtcNow() >= _circuitOpenUntil,
                _maximumConcurrency,
                Volatile.Read(ref _inFlight),
                Interlocked.Read(ref _accepted),
                Interlocked.Read(ref _conflicts),
                Interlocked.Read(ref _timeouts),
                Interlocked.Read(ref _unavailable),
                Interlocked.Read(ref _overloads),
                Interlocked.Read(ref _circuitOpen),
                _lastSuccess);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _multiplexer.CloseAsync(allowCommandsToComplete: false);
        }
        finally
        {
            _multiplexer.Dispose();
            _gate.Dispose();
        }
    }

    private async ValueTask EnterAsync(
        RedisCoordinationOperationFamily family,
        CoordinationDeadline deadline,
        long started,
        CancellationToken cancellationToken)
    {
        var remaining = deadline.Remaining(_timeProvider);
        var admission = remaining < _queueAdmissionTimeout
            ? remaining
            : _queueAdmissionTimeout;
        if (admission <= TimeSpan.Zero)
        {
            Interlocked.Increment(ref _timeouts);
            RedisCoordinationMetrics.Record(
                family,
                RedisCoordinationOutcome.Timeout,
                started);
            throw new RedisCoordinationException(
                CoordinationOperationStatus.DeadlineExceeded);
        }

        bool entered;
        try
        {
            entered = await _gate.WaitAsync(
                admission,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RedisCoordinationMetrics.Record(
                family,
                RedisCoordinationOutcome.Cancelled,
                started);
            throw;
        }
        if (!entered)
        {
            Interlocked.Increment(ref _overloads);
            RedisCoordinationMetrics.Record(
                family,
                RedisCoordinationOutcome.Overloaded,
                started);
            throw new RedisCoordinationException(
                CoordinationOperationStatus.Overloaded);
        }

        Interlocked.Increment(ref _inFlight);
    }

    private void ThrowIfCircuitOpen(
        RedisCoordinationOperationFamily family,
        long started)
    {
        lock (_stateGate)
        {
            if (_timeProvider.GetUtcNow() < _circuitOpenUntil)
            {
                Interlocked.Increment(ref _circuitOpen);
                RedisCoordinationMetrics.Record(
                    family,
                    RedisCoordinationOutcome.CircuitOpen,
                    started);
                throw new RedisCoordinationException(
                    CoordinationOperationStatus.CircuitOpen);
            }
        }
    }

    private void RecordSuccess(
        RedisCoordinationOperationFamily family,
        long started)
    {
        lock (_stateGate)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntil = default;
            _lastSuccess = _timeProvider.GetUtcNow();
        }
        Interlocked.Increment(ref _accepted);
        RedisCoordinationMetrics.Record(
            family,
            RedisCoordinationOutcome.Success,
            started);
    }

    private void RecordFailure(
        RedisCoordinationOperationFamily family,
        RedisCoordinationOutcome outcome,
        long started)
    {
        lock (_stateGate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _circuitOpenUntil =
                    _timeProvider.GetUtcNow() + _circuitOpenDuration;
                _consecutiveFailures = 0;
            }
        }
        RedisCoordinationMetrics.Record(family, outcome, started);
    }

    private TimeSpan EffectiveTimeout(CoordinationDeadline deadline)
    {
        var remaining = deadline.Remaining(_timeProvider);
        return remaining < _operationTimeout
            ? remaining
            : _operationTimeout;
    }

    private async Task ReleaseWhenCompleteAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            ReleasePermit();
        }
    }

    private void ReleasePermit()
    {
        Interlocked.Decrement(ref _inFlight);
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string BoundedClientName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is < 1 or > 64 ||
            trimmed.Any(character =>
                character is < (char)0x21 or > (char)0x7E))
        {
            throw new ArgumentException(
                "Redis client name must be 1..64 printable ASCII bytes.",
                nameof(value));
        }

        return trimmed;
    }

    private static async Task DisposeLateConnectionAsync(
        Task<ConnectionMultiplexer> connect)
    {
        try
        {
            (await connect.ConfigureAwait(false)).Dispose();
        }
        catch
        {
        }
    }
}
