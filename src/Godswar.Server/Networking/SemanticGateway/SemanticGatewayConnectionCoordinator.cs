namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Process-local single-owner gate for active account relays. A newly
/// authenticated login can cancel the prior relay, and a replacement game
/// connection waits for that relay to release its worker lease before it
/// opens another one.
/// </summary>
internal sealed class SemanticGatewayConnectionCoordinator : IDisposable
{
    private readonly Dictionary<int, ActiveConnection> _active = [];
    private readonly object _gate = new();
    private readonly int _maximumConnections;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _replacementTimeout;
    private bool _disposed;

    public SemanticGatewayConnectionCoordinator(
        int maximumConnections,
        TimeSpan replacementTimeout,
        TimeProvider? timeProvider = null)
    {
        if (maximumConnections is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConnections));
        }
        if (replacementTimeout < TimeSpan.FromMilliseconds(100) ||
            replacementTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacementTimeout));
        }

        _maximumConnections = maximumConnections;
        _replacementTimeout = replacementTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Stops an older active relay when a newer login generation reaches the
    /// redirect boundary. A delayed request from an older generation can
    /// never cancel a newer relay.
    /// </summary>
    public bool RequestReplacement(
        int accountId,
        GatewayLoginGenerationId generationId,
        long generationSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        RequireGeneration(generationId, generationSequence);
        ActiveConnection? existing;
        lock (_gate)
        {
            if (_disposed ||
                !_active.TryGetValue(accountId, out existing) ||
                existing.GenerationSequence >= generationSequence)
            {
                return false;
            }
        }

        existing.RequestStop();
        return true;
    }

    /// <summary>
    /// Stops only the relay owned by the exact abandoned or cancelled login
    /// generation. It cannot affect a replacement generation.
    /// </summary>
    public bool RequestStop(
        int accountId,
        GatewayLoginGenerationId generationId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        if (!generationId.IsValid)
        {
            throw new ArgumentException(
                "A valid login-generation ID is required.",
                nameof(generationId));
        }

        ActiveConnection? existing;
        lock (_gate)
        {
            if (_disposed ||
                !_active.TryGetValue(accountId, out existing) ||
                existing.GenerationId != generationId)
            {
                return false;
            }
        }

        existing.RequestStop();
        return true;
    }

    public async ValueTask<SemanticGatewayConnectionLease?> AcquireAsync(
        int accountId,
        GatewayLoginGenerationId generationId,
        long generationSequence,
        GatewayConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        RequireGeneration(generationId, generationSequence);
        if (!connectionId.IsValid)
        {
            throw new ArgumentException(
                "A valid gateway connection ID is required.",
                nameof(connectionId));
        }

        using var deadline = new CancellationTokenSource(
            _replacementTimeout,
            _timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);

        while (true)
        {
            ActiveConnection? existing;
            ActiveConnection? candidate = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_active.TryGetValue(accountId, out existing))
                {
                    if (_active.Count >= _maximumConnections)
                    {
                        return null;
                    }

                    candidate = new ActiveConnection(
                        accountId,
                        generationId,
                        generationSequence,
                        connectionId);
                    _active.Add(accountId, candidate);
                }
                else if (existing.ConnectionId == connectionId ||
                    existing.GenerationSequence >= generationSequence)
                {
                    return null;
                }
            }

            if (candidate is not null)
            {
                return new SemanticGatewayConnectionLease(
                    this,
                    candidate);
            }

            existing!.RequestStop();
            try
            {
                await existing.Released.Task.WaitAsync(
                    lifetime.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        ActiveConnection[] active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            active = _active.Values.ToArray();
            _active.Clear();
        }

        foreach (var entry in active)
        {
            entry.RequestStop();
            entry.Release();
        }
    }

    private void Release(ActiveConnection connection)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(
                    connection.AccountId,
                    out var current) &&
                ReferenceEquals(current, connection))
            {
                _active.Remove(connection.AccountId);
            }
        }

        connection.Release();
    }

    private static void RequireGeneration(
        GatewayLoginGenerationId generationId,
        long generationSequence)
    {
        if (!generationId.IsValid)
        {
            throw new ArgumentException(
                "A valid login-generation ID is required.",
                nameof(generationId));
        }
        if (generationSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generationSequence));
        }
    }

    internal sealed class ActiveConnection(
        int accountId,
        GatewayLoginGenerationId generationId,
        long generationSequence,
        GatewayConnectionId connectionId)
    {
        private readonly CancellationTokenSource _stop = new();
        private int _released;

        public int AccountId { get; } = accountId;

        public GatewayLoginGenerationId GenerationId { get; } =
            generationId;

        public long GenerationSequence { get; } = generationSequence;

        public GatewayConnectionId ConnectionId { get; } = connectionId;

        public CancellationToken ReplacementToken => _stop.Token;

        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RequestStop()
        {
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            Released.TrySetResult();
            _stop.Dispose();
        }
    }

    internal sealed class SemanticGatewayConnectionLease :
        IDisposable
    {
        private SemanticGatewayConnectionCoordinator? _owner;
        private ActiveConnection? _connection;

        public SemanticGatewayConnectionLease(
            SemanticGatewayConnectionCoordinator owner,
            ActiveConnection connection)
        {
            _owner = owner;
            _connection = connection;
        }

        public CancellationToken ReplacementToken =>
            _connection?.ReplacementToken ??
            throw new ObjectDisposedException(
                nameof(SemanticGatewayConnectionLease));

        public GatewayLoginGenerationId GenerationId =>
            _connection?.GenerationId ??
            throw new ObjectDisposedException(
                nameof(SemanticGatewayConnectionLease));

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var connection = Interlocked.Exchange(
                ref _connection,
                null);
            if (owner is not null && connection is not null)
            {
                owner.Release(connection);
            }
        }
    }
}
