using System.Net;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpRuntimeState : byte
{
    Created = 1,
    Starting = 2,
    Ready = 3,
    Stopping = 4,
    Stopped = 5,
    Faulted = 6,
    Disposed = 7
}

internal readonly record struct SecureUdpRuntimeCapabilities(
    bool ProtectedDatagrams,
    bool NativeUdpWorker,
    bool LoopbackEndToEndVerified,
    bool TlsFallbackVerified)
{
    public static SecureUdpRuntimeCapabilities Current =>
        Complete;

    public static SecureUdpRuntimeCapabilities Complete =>
        new(
            ProtectedDatagrams: true,
            NativeUdpWorker: true,
            LoopbackEndToEndVerified: true,
            TlsFallbackVerified: true);

    public bool IsComplete =>
        ProtectedDatagrams &&
        NativeUdpWorker &&
        LoopbackEndToEndVerified &&
        TlsFallbackVerified;

    public void ValidateForActivation()
    {
        if (!IsComplete)
        {
            throw new InvalidDataException(
                "Secure UDP activation requires protected datagrams, the native UDP worker, loopback end-to-end verification, and verified TLS fallback.");
        }
    }
}

internal readonly record struct SecureUdpRuntimeSnapshot(
    SecureUdpRuntimeState State,
    IPEndPoint? LocalEndpoint,
    SecureUdpSessionAuthoritySnapshot Sessions,
    SecureUdpRateLimiterSnapshot Admission,
    string? FailureType)
{
    public bool IsReady =>
        State == SecureUdpRuntimeState.Ready &&
        LocalEndpoint is not null;
}

internal readonly record struct SecureUdpMaintenanceSweep(
    int RemovedSessions,
    SecureUdpKeyRotationSweep KeyRotation);

internal sealed class SecureUdpRuntime : IAsyncDisposable
{
    private readonly SecureUdpAddressValidation _addressValidation;
    private readonly SecureUdpSessionAuthority _authority;
    private readonly TimeSpan _cleanupInterval;
    private readonly SecureUdpEndpointServer _endpoint;
    private readonly TimeSpan _keyRotationAge;
    private readonly ulong _keyRotationPacketLimit;
    private readonly SecureUdpRateLimiter _limiter;
    private readonly Func<SecureUdpMaintenanceSweep>? _maintenanceOverride;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private Exception? _failure;
    private IPEndPoint? _localEndpoint;
    private Task? _runTask;
    private SecureUdpRuntimeState _state = SecureUdpRuntimeState.Created;
    private bool _resourcesDisposed;

    private SecureUdpRuntime(
        SecureUdpAddressValidation addressValidation,
        SecureUdpSessionAuthority authority,
        SecureUdpEndpointServer endpoint,
        SecureUdpRateLimiter limiter,
        TimeSpan cleanupInterval,
        TimeSpan keyRotationAge,
        ulong keyRotationPacketLimit,
        TimeProvider timeProvider,
        Func<SecureUdpMaintenanceSweep>? maintenanceOverride)
    {
        _addressValidation = addressValidation;
        _authority = authority;
        _endpoint = endpoint;
        _limiter = limiter;
        _cleanupInterval = cleanupInterval;
        _keyRotationAge = keyRotationAge;
        _keyRotationPacketLimit = keyRotationPacketLimit;
        _timeProvider = timeProvider;
        _maintenanceOverride = maintenanceOverride;
    }

    public SecureUdpSessionAuthority Authority => _authority;

    public static SecureUdpRuntime? TryCreate(
        SecureNetworkOptions secureOptions,
        SecureGameTarget gameTarget,
        SecureUdpRuntimeCapabilities capabilities,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secureOptions);
        ArgumentNullException.ThrowIfNull(gameTarget);
        if (!secureOptions.Udp.Enabled)
        {
            return null;
        }
        if (!secureOptions.Enabled)
        {
            throw new InvalidDataException(
                "Secure UDP cannot run while the TLS secure transport is disabled.");
        }

        capabilities.ValidateForActivation();
        return CreateCore(
            secureOptions.Udp,
            gameTarget,
            timeProvider ?? TimeProvider.System,
            listenerPortOverride: null,
            maintenanceOverride: null);
    }

    internal static SecureUdpRuntime CreateForLoopbackTest(
        SecureUdpOptions options,
        SecureGameTarget gameTarget,
        TimeProvider? timeProvider = null,
        Func<SecureUdpMaintenanceSweep>? maintenanceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gameTarget);
        if (!IPAddress.TryParse(options.BindHost, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new InvalidDataException(
                "UDP runtime tests may bind only a literal loopback address.");
        }

        return CreateCore(
            options,
            gameTarget,
            timeProvider ?? TimeProvider.System,
            listenerPortOverride: 0,
            maintenanceOverride);
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                _state == SecureUdpRuntimeState.Disposed,
                this);
            if (_runTask is not null)
            {
                throw new InvalidOperationException(
                    "A secure UDP runtime instance can run only once.");
            }

            _state = SecureUdpRuntimeState.Starting;
            _runTask = RunCoreAsync(cancellationToken);
            return _runTask;
        }
    }

    public Task<IPEndPoint> WaitUntilReadyAsync(
        CancellationToken cancellationToken = default)
    {
        return _endpoint.WaitUntilStartedAsync(cancellationToken);
    }

    public SecureUdpRuntimeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (_state == SecureUdpRuntimeState.Disposed)
            {
                return new SecureUdpRuntimeSnapshot(
                    _state,
                    null,
                    default,
                    default,
                    _failure?.GetType().Name);
            }
            return new SecureUdpRuntimeSnapshot(
                _state,
                _localEndpoint,
                _authority.GetSnapshot(),
                _limiter.GetSnapshot(),
                _failure?.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        var cancel = false;
        lock (_sync)
        {
            if (_state == SecureUdpRuntimeState.Disposed)
            {
                return;
            }
            if (_state is SecureUdpRuntimeState.Starting or
                SecureUdpRuntimeState.Ready)
            {
                _state = SecureUdpRuntimeState.Stopping;
            }
            cancel = true;
            runTask = _runTask;
        }
        if (cancel)
        {
            _stop.Cancel();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask;
            }
            catch
            {
                // The runtime task remains the authoritative fault surface.
            }
        }

        lock (_sync)
        {
            if (!_resourcesDisposed)
            {
                _addressValidation.Dispose();
                _authority.Dispose();
                _stop.Dispose();
                _resourcesDisposed = true;
            }
            _state = SecureUdpRuntimeState.Disposed;
            _localEndpoint = null;
        }
    }

    private static SecureUdpRuntime CreateCore(
        SecureUdpOptions options,
        SecureGameTarget gameTarget,
        TimeProvider timeProvider,
        int? listenerPortOverride,
        Func<SecureUdpMaintenanceSweep>? maintenanceOverride)
    {
        SecureUdpSessionAuthority? authority = null;
        SecureUdpAddressValidation? addressValidation = null;
        try
        {
            authority = new SecureUdpSessionAuthority(
                options.SessionCapacity,
                TimeSpan.FromSeconds(options.BindingOfferTtlSeconds),
                TimeSpan.FromSeconds(
                    options.BoundSessionIdleTimeoutSeconds),
                TimeSpan.FromMilliseconds(
                    options.MinimumRebindIntervalMilliseconds),
                gameTarget.ServerId,
                TimeSpan.FromSeconds(
                    options.PreviousKeyEpochOverlapSeconds),
                timeProvider);
            var cookies = new SecureUdpCookieProtector(
                options.BuildCookiePolicy(),
                gameTarget.ServerId,
                checked((ushort)options.Port),
                gameTarget.Audience,
                timeProvider);
            addressValidation = new SecureUdpAddressValidation(
                options.MaximumDatagramBytes,
                cookies);
            var coordinator = new SecureUdpBindingCoordinator(
                addressValidation,
                authority);
            var limiter = new SecureUdpRateLimiter(
                options.GlobalPacketsPerSecond,
                options.UnvalidatedPacketsPerSecond,
                options.PrefixPacketsPerSecond,
                options.RateLimitPrefixCapacity,
                options.BindingProofPacketsPerSecond,
                options.BindingProofPrefixPacketsPerSecond,
                options.ProtectedCandidatePacketsPerSecond,
                options.ProtectedCandidatePrefixPacketsPerSecond,
                options.AuthenticatedSessionPacketsPerSecond,
                options.SessionCapacity,
                timeProvider);
            var endpoint = new SecureUdpEndpointServer(
                options.BindHost,
                listenerPortOverride ?? options.Port,
                options.MaximumDatagramBytes,
                coordinator,
                limiter,
                authority,
                timeProvider);
            return new SecureUdpRuntime(
                addressValidation,
                authority,
                endpoint,
                limiter,
                TimeSpan.FromSeconds(
                    options.SessionCleanupIntervalSeconds),
                TimeSpan.FromSeconds(options.KeyRotationSeconds),
                checked((ulong)options.KeyRotationPacketLimit),
                timeProvider,
                maintenanceOverride);
        }
        catch
        {
            addressValidation?.Dispose();
            authority?.Dispose();
            throw;
        }
    }

    private async Task RunCoreAsync(CancellationToken hostCancellation)
    {
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                hostCancellation,
                _stop.Token);
        var listenerTask = _endpoint.RunAsync(lifetime.Token);
        Task? cleanupTask = null;
        try
        {
            var endpoint = await _endpoint.WaitUntilStartedAsync(
                lifetime.Token);
            lock (_sync)
            {
                _localEndpoint = endpoint;
                _state = SecureUdpRuntimeState.Ready;
            }
            SecureUdpMetrics.RecordRuntimeOutcome(
                SecureUdpRuntimeOutcome.Started);
            cleanupTask = RunCleanupAsync(lifetime.Token);
            var completed = await Task.WhenAny(
                listenerTask,
                cleanupTask);
            lifetime.Cancel();
            await completed;
            await (ReferenceEquals(completed, listenerTask)
                ? cleanupTask
                : listenerTask);
        }
        catch (OperationCanceledException)
            when (hostCancellation.IsCancellationRequested ||
                _stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                _failure = error;
                _state = SecureUdpRuntimeState.Faulted;
            }
            SecureUdpMetrics.RecordRuntimeOutcome(
                SecureUdpRuntimeOutcome.Faulted);
            throw;
        }
        finally
        {
            lifetime.Cancel();
            await ObserveCompletionAsync(listenerTask);
            if (cleanupTask is not null)
            {
                await ObserveCompletionAsync(cleanupTask);
            }

            lock (_sync)
            {
                if (_state != SecureUdpRuntimeState.Faulted)
                {
                    _state = SecureUdpRuntimeState.Stopped;
                }
                _localEndpoint = null;
            }
            SecureUdpMetrics.RecordRuntimeOutcome(
                SecureUdpRuntimeOutcome.Stopped);
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(
                _cleanupInterval,
                _timeProvider,
                cancellationToken);
            var sweep = _maintenanceOverride?.Invoke() ??
                RunMaintenanceSweep();
            if (sweep.KeyRotation.Rotated > 0)
            {
                SecureUdpMetrics.RecordRuntimeOutcome(
                    SecureUdpRuntimeOutcome.KeyRotated,
                    sweep.KeyRotation.Rotated);
            }
            if (sweep.KeyRotation.EpochExhausted > 0)
            {
                SecureUdpMetrics.RecordRuntimeOutcome(
                    SecureUdpRuntimeOutcome.KeyEpochExhausted,
                    sweep.KeyRotation.EpochExhausted);
            }
            if (sweep.RemovedSessions > 0)
            {
                SecureUdpMetrics.RecordRuntimeOutcome(
                    SecureUdpRuntimeOutcome.SessionExpired,
                    sweep.RemovedSessions);
            }
        }
    }

    private SecureUdpMaintenanceSweep RunMaintenanceSweep()
    {
        var removed = _authority.CleanupExpiredSessions();
        var rotation = _authority.RotateProtectedSendKeys(
            _keyRotationPacketLimit,
            _keyRotationAge);
        return new SecureUdpMaintenanceSweep(removed, rotation);
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The first completed runtime task is the authoritative fault.
        }
    }
}
