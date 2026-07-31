using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal sealed record CapturedBackhaulSession(
    GatewayWorldAdmission Admission,
    byte[] EncryptedLogin);

internal sealed class LoopbackBackhaulWorker : IAsyncDisposable
{
    private const int LegacyGameLoginBytes = 36;

    private readonly WorkerBackhaulAdmissionRegistry _admissions;
    private readonly ConcurrentDictionary<long, ILegacyByteTransport>
        _active = [];
    private readonly ConcurrentQueue<CapturedBackhaulSession> _captured = [];
    private readonly ConcurrentQueue<Exception> _errors = [];
    private readonly ConcurrentQueue<BackhaulAdmissionStatus> _rejections = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly WorkerBackhaulTransportFactory _factory;
    private readonly BackhaulHandshakeGate _handshakeGate;
    private readonly TcpListener _listener;
    private readonly byte[] _marker;
    private readonly SemaphoreSlim _rejectionSignal = new(0);
    private readonly SemaphoreSlim _sessionSignal = new(0);
    private readonly ConcurrentDictionary<long, Task> _tasks = [];
    private Task? _acceptTask;
    private long _nextId;
    private long _nextReleaseDelayTicks;

    private LoopbackBackhaulWorker(
        TcpListener listener,
        BackhaulHandshakeGate handshakeGate,
        WorkerBackhaulAdmissionRegistry admissions,
        WorkerBackhaulTransportFactory factory,
        byte[] marker)
    {
        _listener = listener;
        _handshakeGate = handshakeGate;
        _admissions = admissions;
        _factory = factory;
        _marker = marker;
    }

    public IPEndPoint Endpoint =>
        (IPEndPoint)_listener.LocalEndpoint;

    public int SessionCount => _captured.Count;

    public static Task<LoopbackBackhaulWorker> StartAsync(
        ServerNodeId node,
        SemanticGatewayRouteTarget route,
        X509Certificate2 workerCertificate,
        X509Certificate2 gatewayCertificate,
        byte[] marker)
    {
        var listener = new TcpListener(
            IPAddress.Loopback,
            0);
        listener.Start(16);
        BackhaulHandshakeGate? gate = null;
        WorkerBackhaulAdmissionRegistry? admissions = null;
        try
        {
            gate = new BackhaulHandshakeGate(8);
            admissions = new WorkerBackhaulAdmissionRegistry(
                node,
                [
                    new BackhaulOwnedWorldRoute(
                        route.RealmId,
                        route.MapId,
                        route.WorldInstanceId)
                ],
                capacity: 16,
                replayCapacity: 64,
                replayRetention: TimeSpan.FromMinutes(1),
                futureClockSkew: TimeSpan.FromSeconds(5));
            var factory = new WorkerBackhaulTransportFactory(
                workerCertificate,
                new BackhaulCertificatePins(
                    [
                        BackhaulCertificatePins.FingerprintOf(
                            gatewayCertificate)
                    ]),
                gate,
                admissions,
                BackhaulRuntimeLimits.Default);
            var worker = new LoopbackBackhaulWorker(
                listener,
                gate,
                admissions,
                factory,
                marker.ToArray());
            worker._acceptTask = worker.AcceptAsync();
            return Task.FromResult(worker);
        }
        catch
        {
            admissions?.Dispose();
            gate?.Dispose();
            listener.Stop();
            throw;
        }
    }

    public async Task<CapturedBackhaulSession> WaitForSessionAsync(
        int count,
        CancellationToken cancellationToken)
    {
        while (_captured.Count < count)
        {
            await _sessionSignal.WaitAsync(cancellationToken);
            if (_errors.TryDequeue(out var error))
            {
                throw new InvalidOperationException(
                    "Loopback backhaul worker failed.",
                    error);
            }
        }

        return _captured.ToArray()[count - 1];
    }

    public void DropActiveSessions()
    {
        foreach (var transport in _active.Values)
        {
            transport.Disconnect();
        }
    }

    public void DelayNextRelease(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero ||
            delay > TimeSpan.FromSeconds(2))
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        Interlocked.Exchange(
            ref _nextReleaseDelayTicks,
            delay.Ticks);
    }

    public async Task<BackhaulAdmissionStatus> WaitForRejectionAsync(
        CancellationToken cancellationToken)
    {
        BackhaulAdmissionStatus status;
        while (!_rejections.TryDequeue(out status))
        {
            await _rejectionSignal.WaitAsync(cancellationToken);
        }

        return status;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        DropActiveSessions();
        if (_acceptTask is not null)
        {
            await IgnoreAsync(_acceptTask);
        }
        var tasks = _tasks.Values.ToArray();
        if (tasks.Length != 0)
        {
            await Task.WhenAll(tasks);
        }
        _rejectionSignal.Dispose();
        _sessionSignal.Dispose();
        _admissions.Dispose();
        _handshakeGate.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(
                    _stop.Token);
            }
            catch (Exception error)
                when (_stop.IsCancellationRequested &&
                    error is OperationCanceledException or SocketException)
            {
                return;
            }

            var id = Interlocked.Increment(ref _nextId);
            var task = HandleAsync(id, client);
            _tasks[id] = task;
            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    _tasks.TryRemove(id, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(long id, TcpClient client)
    {
        ILegacyByteTransport? transport = null;
        try
        {
            transport = await _factory.CreateAsync(
                client,
                NetworkEndpointRole.Game,
                Stopwatch.GetTimestamp(),
                _stop.Token);
            _active[id] = transport;
            var login = new byte[LegacyGameLoginBytes];
            await ReadExactlyAsync(
                transport,
                login,
                _stop.Token);
            var typed = transport as WorkerBackhaulLegacyTransport
                ?? throw new InvalidOperationException(
                    "Worker factory returned an unexpected transport.");
            _captured.Enqueue(
                new CapturedBackhaulSession(
                    typed.WorldAdmission,
                    login));
            _sessionSignal.Release();
            await transport.WriteAsync(
                _marker,
                _stop.Token);

            var buffer = new byte[4 * 1024];
            while (true)
            {
                var read = await transport.ReadAsync(
                    buffer,
                    _stop.Token);
                if (read == 0)
                {
                    return;
                }
                await transport.WriteAsync(
                    buffer.AsMemory(0, read),
                    _stop.Token);
            }
        }
        catch (WorkerBackhaulAdmissionException error)
        {
            _rejections.Enqueue(error.Status);
            _rejectionSignal.Release();
        }
        catch (Exception error)
            when (_stop.IsCancellationRequested ||
                error is IOException or
                    SocketException or
                    OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _errors.Enqueue(error);
            _sessionSignal.Release();
        }
        finally
        {
            _active.TryRemove(id, out _);
            if (transport is not null)
            {
                var delayTicks = Interlocked.Exchange(
                    ref _nextReleaseDelayTicks,
                    0);
                if (delayTicks > 0)
                {
                    await Task.Delay(
                        TimeSpan.FromTicks(delayTicks));
                }
                await transport.DisposeAsync();
            }
            else
            {
                client.Dispose();
            }
        }
    }

    private static async Task ReadExactlyAsync(
        ILegacyByteTransport transport,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await transport.ReadAsync(
                destination[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Gateway closed before the legacy login packet.");
            }
            offset += read;
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
}
