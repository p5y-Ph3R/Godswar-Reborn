using System.Buffers;
using System.Net.Security;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport :
    ILegacyByteTransport,
    ISecureControlChannel,
    ISecureCommandOperationTransport
{
    private readonly Action _abortConnection;
    private readonly IDisposable _connectionOwner;
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource _authenticated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BoundedByteQueue<SecureControlWork> _controlQueue;
    private readonly SecureConnectionContext _connectionContext;
    private readonly Task _controlTask;
    private readonly NetworkEndpointRole _endpointRole;
    private readonly BoundedByteQueue<SecureLegacyChunk> _ingress;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NetworkRuntimeOptions _options;
    private readonly Task _readerTask;
    private readonly Task _heartbeatTask;
    private readonly SecureEndpointRole _secureRole;
    private readonly SecureBoundGamePrincipal? _boundGamePrincipal;
    private readonly SslStream _stream;
    private readonly TimeProvider _timeProvider;
    private SecureUdpSessionLease? _udpRegistrationLease;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _heartbeatGate = new();
    private readonly object _packetOperationGate = new();
    private SecureLegacyChunk? _currentChunk;
    private int _currentChunkOffset;
    private int _disconnectStarted;
    private int _gameGrantStarted;
    private Task? _disposeTask;
    private ulong _nextInboundSequence = 1;
    private ulong _nextOutboundSequence = 1;
    private bool _heartbeatActive;
    private bool _pingOutstanding;
    private long _lastReceiveTimestamp;
    private long _lastSendTimestamp;
    private long _pingTimestamp;
    private readonly byte[] _pingNonce = new byte[8];
    private bool _packetReadActive;
    private bool _packetReadHasBytes;
    private SecureLegacyCommandOperation? _packetOperation;

    public TlsMuxLegacyTransport(
        IDisposable connectionOwner,
        Action abortConnection,
        SslStream stream,
        string remoteEndPoint,
        NetworkEndpointRole endpointRole,
        SecureEndpointRole secureRole,
        NetworkRuntimeOptions options,
        TimeProvider? timeProvider,
        SecureConnectionContext connectionContext,
        SecureBoundGamePrincipal? boundGamePrincipal,
        SecureUdpSessionLease? udpRegistrationLease = null)
    {
        ArgumentNullException.ThrowIfNull(connectionOwner);
        ArgumentNullException.ThrowIfNull(abortConnection);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionContext);
        if (connectionContext.Role != secureRole)
        {
            throw new ArgumentException(
                "The secure connection context role must match the transport role.",
                nameof(connectionContext));
        }
        if ((endpointRole == NetworkEndpointRole.Game) !=
                (secureRole == SecureEndpointRole.Game) ||
            endpointRole == NetworkEndpointRole.Game &&
                boundGamePrincipal is null ||
            endpointRole == NetworkEndpointRole.Login &&
                boundGamePrincipal is not null ||
            udpRegistrationLease is not null &&
                boundGamePrincipal is null)
        {
            throw new ArgumentException(
                "Only a successfully bound secure game transport may carry a game principal.",
                nameof(boundGamePrincipal));
        }
        if (!SecureTlsPolicy.IsNegotiationAccepted(stream))
        {
            throw new InvalidOperationException(
                "A TLS mux transport requires an accepted TLS negotiation.");
        }

        _connectionOwner = connectionOwner;
        _abortConnection = abortConnection;
        _stream = stream;
        _endpointRole = endpointRole;
        _secureRole = secureRole;
        _connectionContext = connectionContext;
        _boundGamePrincipal = boundGamePrincipal;
        _udpRegistrationLease = udpRegistrationLease;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (boundGamePrincipal is not null)
        {
            _nextInboundSequence = 2;
            _nextOutboundSequence = 2;
        }
        _ingress = new BoundedByteQueue<SecureLegacyChunk>(
            options.IngressQueueItems,
            options.IngressQueueBytes);
        _controlQueue = new BoundedByteQueue<SecureControlWork>(
            options.ControlQueueItems,
            options.ControlQueueBytes);
        _controlTask = RunControlAsync();
        _readerTask = RunReaderAsync();
        _heartbeatTask = RunHeartbeatAsync();
    }

    public string RemoteEndPoint => "secure";

    public SecureConnectionContext ConnectionContext => _connectionContext;

    public SecureBoundGamePrincipal? BoundGamePrincipal =>
        _boundGamePrincipal;

    public bool SupportsRealtimeMovement =>
        _udpRegistrationLease?.SupportsRealtimeMovement == true;

    public bool IsRealtimeMovementActive =>
        _udpRegistrationLease?.IsRealtimeMovementActive == true;

    public bool TryTakeRealtimeMovement(
        out SecureRealtimeMovementIngress ingress)
    {
        var lease = Volatile.Read(ref _udpRegistrationLease);
        if (lease is not null &&
            lease.TryTakeRealtimeMovement(out ingress))
        {
            return true;
        }

        ingress = default;
        return false;
    }

    public bool TryPublishRealtimeSnapshot(
        in SecureRealtimePositionSnapshot snapshot)
    {
        var lease = Volatile.Read(ref _udpRegistrationLease);
        return lease is not null &&
            lease.TryPublishRealtimeSnapshot(snapshot);
    }
    internal BoundedByteQueueSnapshot IngressSnapshot =>
        _ingress.Snapshot();

    internal BoundedByteQueueSnapshot ControlSnapshot =>
        _controlQueue.Snapshot();

    internal bool PingOutstanding
    {
        get
        {
            lock (_heartbeatGate)
            {
                return _pingOutstanding;
            }
        }
    }

    public void MarkAuthenticated()
    {
        lock (_heartbeatGate)
        {
            if (_heartbeatActive)
            {
                return;
            }

            var now = _timeProvider.GetTimestamp();
            _lastReceiveTimestamp = now;
            _lastSendTimestamp = now;
            _heartbeatActive = true;
        }
        _authenticated.TrySetResult();
        ControlledHostPrivacyEvidence.RecordIfActive(
            ControlledHostEvidenceEvent.TlsClientAuthenticated);
    }

    public async ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        while (_currentChunk is null)
        {
            using var readLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
            DequeueResult<SecureLegacyChunk> result;
            try
            {
                result = await _ingress.DequeueAsync(readLifetime.Token);
            }
            catch (OperationCanceledException)
                when (_lifetime.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            if (!result.HasItem)
            {
                return 0;
            }

            SecureNetworkMetrics.IngressRemoved(
                _endpointRole,
                itemCount: 1,
                result.ByteCount);
            if (result.Item.IsOperationMetadata)
            {
                AcceptOperationMetadata(result.Item.Operation);
                result.Item.Return();
                continue;
            }
            _currentChunk = result.Item;
            _currentChunkOffset = 0;
        }

        var count = Math.Min(
            destination.Length,
            _currentChunk.Length - _currentChunkOffset);
        _currentChunk.Buffer
            .AsMemory(_currentChunkOffset, count)
            .CopyTo(destination);
        MarkPacketBytesRead();
        _currentChunkOffset += count;
        if (_currentChunkOffset == _currentChunk.Length)
        {
            _currentChunk.Return();
            _currentChunk = null;
            _currentChunkOffset = 0;
        }

        return count;
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnectStarted, 1) != 0)
        {
            return;
        }

        _ingress.Complete();
        _controlQueue.Complete();
        CancelLifetime();
        ReleaseUdpRegistration();
        CloseSocket();
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            disposeTask = _disposeTask ??= DisposeOwnedResourcesAsync();
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeOwnedResourcesAsync()
    {
        Disconnect();
        try
        {
            await _readerTask;
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _controlQueue.Complete();
            await AwaitBackgroundTaskAsync(_controlTask);
            await AwaitBackgroundTaskAsync(_heartbeatTask);
            lock (_heartbeatGate)
            {
                CryptographicOperations.ZeroMemory(_pingNonce);
                _pingOutstanding = false;
                _pingTimestamp = 0;
            }
            _currentChunk?.Return();
            _currentChunk = null;
            DrainIngress();
            ReleaseUdpRegistration();
            _writeGate.Dispose();
            _lifetime.Dispose();
            await _stream.DisposeAsync();
            _connectionOwner.Dispose();
        }
    }

    private void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.Exchange(ref _disconnectStarted, 1) != 0)
        {
            return;
        }

        _ingress.Complete(error);
        _controlQueue.Complete(error);
        CancelLifetime();
        ReleaseUdpRegistration();
        CloseSocket();
    }

    private void DrainIngress()
    {
        var drained = _ingress.TryDrain();
        if (drained.Count > 0)
        {
            SecureNetworkMetrics.IngressRemoved(
                _endpointRole,
                drained.Count,
                drained.Sum(static entry => (long)entry.ByteCount));
        }
        foreach (var entry in drained)
        {
            entry.Item.Return();
        }
    }

    private static async Task AwaitBackgroundTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (BoundedByteQueueCompletedException)
        {
        }
        catch (SecureTransportException)
        {
        }
    }

    private void CancelLifetime()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CloseSocket()
    {
        try
        {
            _abortConnection();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ReleaseUdpRegistration()
    {
        Interlocked.Exchange(ref _udpRegistrationLease, null)?.Dispose();
    }

    private sealed class SecureLegacyChunk
    {
        private byte[]? _buffer;
        private readonly SecureLegacyCommandOperation? _operation;

        public SecureLegacyChunk(byte[] buffer, int length)
        {
            _buffer = buffer;
            Length = length;
        }

        public SecureLegacyChunk(
            SecureLegacyCommandOperation operation)
        {
            _operation = operation;
        }

        public byte[] Buffer => _buffer ??
            throw new ObjectDisposedException(nameof(SecureLegacyChunk));

        public int Length { get; }

        public bool IsOperationMetadata => _operation.HasValue;

        public SecureLegacyCommandOperation Operation =>
            _operation ??
            throw new InvalidOperationException(
                "This ingress item contains legacy bytes.");

        public void Return()
        {
            var owned = Interlocked.Exchange(ref _buffer, null);
            if (owned is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(owned.AsSpan(0, Length));
            ArrayPool<byte>.Shared.Return(owned);
        }
    }

    private sealed class SecureControlWork(byte[] payload)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Payload { get; } = payload;

        public Task Completion => _completion.Task;

        public void SetResult() => _completion.TrySetResult();

        public void SetException(Exception error) =>
            _completion.TrySetException(error);

        public void Clear()
        {
            CryptographicOperations.ZeroMemory(Payload);
        }
    }
}
