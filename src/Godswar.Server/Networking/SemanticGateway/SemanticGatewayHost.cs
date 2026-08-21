using System.Net;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

internal readonly record struct SemanticGatewayStartedEndpoints(
    IPEndPoint Login,
    IPEndPoint Game);

/// <summary>
/// Composes the local legacy edge and authenticated private worker backhaul.
/// The host owns no ECS or durable player state. It owns and disposes the
/// supplied coordination implementation for its full listener lifetime.
/// </summary>
internal sealed class SemanticGatewayHost : IAsyncDisposable
{
    private readonly ISemanticGatewayCoordination _coordination;
    private readonly TimeSpan _coordinationTimeout;
    private readonly SemanticGatewayRuntimeConfiguration _configuration;
    private readonly SemanticGatewayGameServer _gameServer;
    private readonly BackhaulHandshakeGate _handshakeGate;
    private readonly SemanticGatewayConnectionCoordinator
        _connections;
    private readonly TcpEndpointServer _loginServer;
    private readonly IConnectionAdmission _admission;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _disposeStop = new();
    private readonly TaskCompletionSource<SemanticGatewayStartedEndpoints>
        _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _draining;
    private int _runStarted;

    public SemanticGatewayHost(
        SemanticGatewayRuntimeConfiguration configuration,
        ISemanticGatewayDataSession data,
        TimeProvider? timeProvider = null,
        ISemanticGatewayCoordination? coordination = null)
    {
        _configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        ArgumentNullException.ThrowIfNull(data);
        var clock = timeProvider ?? TimeProvider.System;
        _timeProvider = clock;

        _coordination = coordination ??
            new InMemorySemanticGatewayCoordination(
                new SemanticGatewayAdmissionAuthority(
                    configuration.RouteDirectory,
                    configuration.AuthorityLimits,
                    clock),
                clock);
        _coordinationTimeout = CoordinationTimeout(
            configuration.ClientLimits.FirstPacketTimeout);
        _connections = new SemanticGatewayConnectionCoordinator(
            configuration.AuthorityLimits.MaximumAdmissions,
            configuration.ClientLimits.GracefulDrainTimeout,
            clock);
        _admission = new ConnectionAdmission(
            configuration.ClientLimits.AdmissionOptions);
        var network = configuration.ClientLimits.CreateNetworkOptions();
        _loginServer = new TcpEndpointServer(
            NetworkEndpointRole.Login,
            configuration.LoginBind.Address.ToString(),
            configuration.LoginBind.Port,
            network,
            _admission,
            session => new SemanticGatewayLoginHandler(
                session,
                data,
                _coordination,
                _connections,
                _coordinationTimeout,
                clock),
            clock);
        _handshakeGate =
            configuration.CreateBackhaulHandshakeGate();
        var dependencies =
            new SemanticGatewayGameConnectionDependencies(
                data,
                _coordination,
                _connections,
                (realm, map) => configuration.TryResolveMap(
                        realm,
                        map,
                        out var target)
                    ? target
                    : null,
                realm => configuration.TryResolveBootstrap(
                        realm,
                        out var target)
                    ? target
                    : null,
                node => configuration.TryGetWorker(
                        node,
                        out var worker)
                    ? worker
                    : null,
                configuration.GatewayCertificate,
                _handshakeGate,
                configuration.BackhaulLimits,
                configuration.ClientLimits.FirstPacketTimeout,
                configuration.ClientLimits.IdleTimeout,
                RefreshInterval(
                    configuration.AuthorityLimits
                        .CommittedAdmissionTtl),
                _coordinationTimeout,
                configuration.ClientLimits.BufferSizeBytes,
                clock);
        _gameServer = new SemanticGatewayGameServer(
            configuration.GameBind,
            network,
            _admission,
            dependencies);
    }

    public SemanticGatewayAuthoritySnapshot AuthoritySnapshot =>
        _coordination.GetSnapshot();

    public SemanticGatewayGameSnapshot GameSnapshot =>
        _gameServer.GetSnapshot();

    public Task<SemanticGatewayStartedEndpoints> WaitUntilStartedAsync(
        CancellationToken cancellationToken = default) =>
        _started.Task.WaitAsync(cancellationToken);

    public void BeginDrain()
    {
        if (Interlocked.Exchange(ref _draining, 1) == 0)
        {
            _admission.BeginDrain();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A semantic gateway host can run only once.");
        }

        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeStop.Token);
        var login = _loginServer.RunAsync(lifetime.Token);
        var game = _gameServer.RunAsync(lifetime.Token);
        var sweep = SweepExpiredAsync(lifetime.Token);
        try
        {
            var endpoints = new SemanticGatewayStartedEndpoints(
                await _loginServer.WaitUntilStartedAsync(
                    lifetime.Token),
                await _gameServer.WaitUntilStartedAsync(
                    lifetime.Token));
            _started.TrySetResult(endpoints);

            var first = await Task.WhenAny(login, game, sweep);
            if (!lifetime.IsCancellationRequested)
            {
                lifetime.Cancel();
                await IgnoreAsync(login);
                await IgnoreAsync(game);
                await IgnoreAsync(sweep);
                throw new InvalidOperationException(
                    first == sweep
                        ? "The semantic gateway coordination sweeper " +
                            "stopped before shutdown."
                        : "A semantic gateway listener stopped before " +
                            "shutdown.");
            }

            await Task.WhenAll(login, game, sweep);
        }
        catch (OperationCanceledException)
            when (lifetime.IsCancellationRequested)
        {
            _started.TrySetCanceled(lifetime.Token);
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            throw;
        }
        finally
        {
            BeginDrain();
            lifetime.Cancel();
            await IgnoreAsync(login);
            await IgnoreAsync(game);
            await IgnoreAsync(sweep);
            _stopped.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        BeginDrain();
        try
        {
            _disposeStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (Volatile.Read(ref _runStarted) != 0)
        {
            try
            {
                await _stopped.Task.WaitAsync(
                    _configuration.ClientLimits.GracefulDrainTimeout);
            }
            catch (TimeoutException)
            {
            }
        }
        else
        {
            _started.TrySetException(
                new ObjectDisposedException(
                    nameof(SemanticGatewayHost)));
            _stopped.TrySetResult();
        }

        try
        {
            await _gameServer.DisposeAsync();
        }
        finally
        {
            try
            {
                await _coordination.DisposeAsync();
            }
            finally
            {
                _connections.Dispose();
                _handshakeGate.Dispose();
                _disposeStop.Dispose();
            }
        }
    }

    private async Task SweepExpiredAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(1),
                cancellationToken);
            for (var pass = 0; pass < 16; pass++)
            {
                var swept =
                    await _coordination.SweepExpiredAsync(
                        CoordinationDeadline.FromNow(
                            _coordinationTimeout,
                            _timeProvider),
                        cancellationToken);
                if (swept == 0)
                {
                    break;
                }
            }
        }
    }

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static TimeSpan RefreshInterval(TimeSpan ttl)
    {
        var milliseconds = Math.Max(
            250,
            ttl.TotalMilliseconds / 2);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan CoordinationTimeout(
        TimeSpan firstPacketTimeout)
    {
        var milliseconds = Math.Min(
            TimeSpan.FromSeconds(2).TotalMilliseconds,
            firstPacketTimeout.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
    }
}
