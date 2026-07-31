using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

internal sealed record SemanticGatewayGameConnectionDependencies(
    ISemanticGatewayDataSession Data,
    SemanticGatewayAdmissionAuthority Authority,
    SemanticGatewayConnectionCoordinator Connections,
    Func<MapId, SemanticGatewayRouteTarget?> ResolveMapRoute,
    SemanticGatewayRouteTarget BootstrapRoute,
    Func<ServerNodeId, SemanticGatewayWorkerTarget?> ResolveWorker,
    X509Certificate2 GatewayCertificate,
    BackhaulHandshakeGate HandshakeGate,
    BackhaulRuntimeLimits BackhaulLimits,
    TimeSpan FirstPacketTimeout,
    TimeSpan IdleTimeout,
    TimeSpan AdmissionRefreshInterval,
    int BufferSizeBytes,
    TimeProvider TimeProvider);

internal static partial class SemanticGatewayGameConnection
{
    public static async Task<SemanticGatewayGameOutcome> RunAsync(
        TcpClient client,
        ConnectionAdmissionLease admissionLease,
        SemanticGatewayGameConnectionDependencies dependencies,
        SemanticGatewayGameMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(admissionLease);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(metrics);

        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        if (remote is null || remote.Port is < 1 or > ushort.MaxValue)
        {
            return SemanticGatewayGameOutcome.ProtocolRejected;
        }

        LegacyGameLoginProbeResult probe;
        try
        {
            probe = await LegacyGameLoginProbe.ReadAsync(
                client.GetStream(),
                dependencies.FirstPacketTimeout,
                dependencies.TimeProvider,
                cancellationToken);
        }
        catch (Exception ex)
            when (ex is InvalidDataException or
                EndOfStreamException or
                TimeoutException)
        {
            return ex is TimeoutException
                ? SemanticGatewayGameOutcome.IdleTimeout
                : SemanticGatewayGameOutcome.ProtocolRejected;
        }

        try
        {
            var login = dependencies.Authority.TryFindLogin(
                probe.Username,
                remote.Address);
            if (!login.IsFound || login.Generation is null)
            {
                return SemanticGatewayGameOutcome.LoginNotFound;
            }

            var principal = login.Generation.Principal;
            SemanticGatewayCharacterRoute? character;
            try
            {
                character = await dependencies.Data.FindCharacterRouteAsync(
                    principal.AccountId,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return SemanticGatewayGameOutcome.ServerShutdown;
            }
            catch
            {
                return SemanticGatewayGameOutcome.CharacterUnavailable;
            }

            var target = character is null
                ? dependencies.BootstrapRoute
                : dependencies.ResolveMapRoute(character.MapId);
            if (target is null)
            {
                return SemanticGatewayGameOutcome.RouteUnavailable;
            }

            SemanticGatewayConnectionSource source;
            try
            {
                source = new SemanticGatewayConnectionSource(
                    GatewayConnectionId.New(),
                    remote.Address);
            }
            catch (ArgumentException)
            {
                return SemanticGatewayGameOutcome.ProtocolRejected;
            }

            var reserved = dependencies.Authority.Reserve(
                login.Generation.GenerationId,
                principal,
                source,
                target.Value);
            if (reserved.Status !=
                    SemanticGatewayAdmissionStatus.Reserved ||
                reserved.Admission is null)
            {
                return SemanticGatewayGameOutcome.AdmissionRejected;
            }

            var gatewayAdmission = reserved.Admission;
            var claim = CreateClaim(gatewayAdmission);
            var committed = false;
            SemanticGatewayConnectionCoordinator
                .SemanticGatewayConnectionLease? connectionLease = null;
            try
            {
                connectionLease =
                    await dependencies.Connections.AcquireAsync(
                        principal.AccountId,
                        login.Generation.GenerationId,
                        login.Generation.Sequence,
                        source.ConnectionId,
                        cancellationToken);
                if (connectionLease is null)
                {
                    return SemanticGatewayGameOutcome.AdmissionRejected;
                }
                using var relayLifetime =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        connectionLease.ReplacementToken);

                var worker =
                    dependencies.ResolveWorker(
                        gatewayAdmission.Route.NodeId);
                if (worker is null ||
                    worker.NodeId != gatewayAdmission.Route.NodeId)
                {
                    return SemanticGatewayGameOutcome.RouteUnavailable;
                }

                var workerAdmission = CreateWorkerAdmission(
                    gatewayAdmission,
                    character?.CharacterId ?? 0,
                    remote);
                await using var backhaul =
                    await ConnectToWorkerAsync(
                        worker.Endpoint,
                        worker.TlsHost,
                        dependencies.GatewayCertificate,
                        worker.CertificatePins,
                        dependencies.HandshakeGate,
                        workerAdmission,
                        dependencies.BackhaulLimits,
                        dependencies.TimeProvider,
                        relayLifetime.Token);

                var commit = dependencies.Authority.Commit(claim);
                if (commit.Status !=
                    SemanticGatewayAdmissionStatus.Committed)
                {
                    return SemanticGatewayGameOutcome.AdmissionRejected;
                }
                committed = true;
                admissionLease.MarkAuthenticated();

                await backhaul.WriteAsync(
                    probe.EncryptedPacket,
                    relayLifetime.Token);
                metrics.RecordBytes(
                    clientToWorker: true,
                    probe.EncryptedPacket.Length);
                return await PumpAsync(
                    client,
                    backhaul,
                    claim,
                    connectionLease.ReplacementToken,
                    dependencies,
                    metrics,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                    connectionLease is not null &&
                    connectionLease.ReplacementToken
                        .IsCancellationRequested)
            {
                return SemanticGatewayGameOutcome.AdmissionRejected;
            }
            catch (BackhaulAdmissionRejectedException)
            {
                return SemanticGatewayGameOutcome.AdmissionRejected;
            }
            catch (BackhaulTimeoutException)
            {
                return SemanticGatewayGameOutcome.WorkerUnavailable;
            }
            catch (Exception ex)
                when (ex is AuthenticationException or
                    InvalidDataException or
                    IOException or
                    SocketException)
            {
                return SemanticGatewayGameOutcome.WorkerUnavailable;
            }
            finally
            {
                connectionLease?.Dispose();
                if (committed)
                {
                    dependencies.Authority.Release(claim);
                }
                else
                {
                    dependencies.Authority.Rollback(claim);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                probe.EncryptedPacket);
        }
    }

    private static async Task<SemanticGatewayGameOutcome> PumpAsync(
        TcpClient client,
        GatewayBackhaulConnection backhaul,
        SemanticGatewayAdmissionClaim claim,
        CancellationToken replacementToken,
        SemanticGatewayGameConnectionDependencies dependencies,
        SemanticGatewayGameMetrics metrics,
        CancellationToken cancellationToken)
    {
        using var stop =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                replacementToken);
        var activity = new ConnectionActivity();
        var clientToWorker = PumpClientToWorkerAsync(
            client.GetStream(),
            backhaul,
            dependencies.BufferSizeBytes,
            activity,
            metrics,
            stop.Token);
        var workerToClient = PumpWorkerToClientAsync(
            backhaul,
            client.GetStream(),
            dependencies.BufferSizeBytes,
            dependencies.BackhaulLimits.WriteTimeout,
            dependencies.TimeProvider,
            activity,
            metrics,
            stop.Token);
        var idle = WaitForIdleAsync(
            activity,
            dependencies.IdleTimeout,
            dependencies.TimeProvider,
            stop.Token);
        var refresh = RefreshAdmissionAsync(
            dependencies.Authority,
            claim,
            dependencies.AdmissionRefreshInterval,
            dependencies.TimeProvider,
            stop.Token);

        var first = await Task.WhenAny(
            clientToWorker,
            workerToClient,
            idle,
            refresh);
        var outcome = cancellationToken.IsCancellationRequested
            ? SemanticGatewayGameOutcome.ServerShutdown
            : ReferenceEquals(first, idle)
                ? SemanticGatewayGameOutcome.IdleTimeout
                : ReferenceEquals(first, refresh)
                    ? SemanticGatewayGameOutcome.AdmissionRejected
                    : first.IsCompletedSuccessfully
                        ? SemanticGatewayGameOutcome.Completed
                        : SemanticGatewayGameOutcome.TransportError;

        stop.Cancel();
        backhaul.Disconnect();
        client.Dispose();
        await IgnoreAsync(clientToWorker);
        await IgnoreAsync(workerToClient);
        await IgnoreAsync(idle);
        await IgnoreAsync(refresh);
        return outcome;
    }

    private static async Task PumpClientToWorkerAsync(
        NetworkStream client,
        GatewayBackhaulConnection worker,
        int bufferSize,
        ConnectionActivity activity,
        SemanticGatewayGameMetrics metrics,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var count = await client.ReadAsync(
                    buffer.AsMemory(0, bufferSize),
                    cancellationToken);
                if (count == 0)
                {
                    return;
                }

                activity.Touch();
                await worker.WriteAsync(
                    buffer.AsMemory(0, count),
                    cancellationToken);
                activity.Touch();
                metrics.RecordBytes(clientToWorker: true, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task PumpWorkerToClientAsync(
        GatewayBackhaulConnection worker,
        NetworkStream client,
        int bufferSize,
        TimeSpan writeTimeout,
        TimeProvider timeProvider,
        ConnectionActivity activity,
        SemanticGatewayGameMetrics metrics,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var count = await worker.ReadAsync(
                    buffer.AsMemory(0, bufferSize),
                    cancellationToken);
                if (count == 0)
                {
                    return;
                }

                activity.Touch();
                using var deadline = new CancellationTokenSource(
                    writeTimeout,
                    timeProvider);
                using var lifetime =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        deadline.Token);
                await client.WriteAsync(
                    buffer.AsMemory(0, count),
                    lifetime.Token);
                activity.Touch();
                metrics.RecordBytes(clientToWorker: false, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task WaitForIdleAsync(
        ConnectionActivity activity,
        TimeSpan idleTimeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromSeconds(1);
        while (true)
        {
            var remaining = idleTimeout - activity.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(
                remaining < poll ? remaining : poll,
                timeProvider,
                cancellationToken);
        }
    }

    private static async Task RefreshAdmissionAsync(
        SemanticGatewayAdmissionAuthority authority,
        SemanticGatewayAdmissionClaim claim,
        TimeSpan interval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(
                interval,
                timeProvider,
                cancellationToken);
            var refreshed = authority.RefreshCommitted(claim);
            if (refreshed.Status !=
                SemanticGatewayAdmissionStatus.Refreshed)
            {
                throw new InvalidDataException(
                    "The gateway admission is no longer authoritative.");
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

    private static SemanticGatewayAdmissionClaim CreateClaim(
        SemanticGatewayAdmissionLease admission) =>
        new(
            admission.AdmissionId,
            admission.GenerationId,
            admission.Principal,
            admission.Source,
            admission.Route.Target,
            admission.Route.NodeId,
            admission.Route.WorkerRevision);

    private static GatewayWorldAdmission CreateWorkerAdmission(
        SemanticGatewayAdmissionLease admission,
        int characterId,
        IPEndPoint observedSource) =>
        new(
            SemanticGatewayProcessIdentity.BootId,
            admission.Source.ConnectionId.Value,
            admission.GenerationId.Value,
            admission.Principal.AccountId,
            characterId,
            admission.Principal.CanonicalUsername!,
            admission.Route.Target.RealmId,
            admission.Route.Target.MapId,
            admission.Route.Target.WorldInstanceId,
            admission.Route.NodeId,
            WholeMillisecond(admission.ReservedAt),
            WholeMillisecond(admission.ExpiresAt),
            observedSource);

    private static DateTimeOffset WholeMillisecond(
        DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            value.ToUnixTimeMilliseconds());

    private sealed class ConnectionActivity
    {
        private long _last = Stopwatch.GetTimestamp();

        public TimeSpan Elapsed =>
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _last));

        public void Touch() =>
            Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp());
    }
}

internal static class SemanticGatewayProcessIdentity
{
    public static Guid BootId { get; } =
        SemanticGatewayIdFactory.NewGuid();
}
