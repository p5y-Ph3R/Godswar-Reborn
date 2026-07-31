using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    private static SemanticGatewayRuntimeConfiguration
        CreateHostConfiguration(
            X509Certificate2 gatewayCertificate,
            X509Certificate2 workerCertificateA,
            X509Certificate2 workerCertificateB,
            IPEndPoint endpointA,
            IPEndPoint endpointB)
    {
        var routeA = IntegrationRouteA();
        var routeB = IntegrationRouteB();
        var routes = new StaticSemanticGatewayRouteDirectory(
            [
                new SemanticGatewayWorkerDefinition(
                    IntegrationNodeA,
                    8),
                new SemanticGatewayWorkerDefinition(
                    IntegrationNodeB,
                    8)
            ],
            [
                new SemanticGatewayStaticRoute(
                    routeA.RealmId,
                    routeA.MapId,
                    routeA.WorldInstanceId,
                    IntegrationNodeA,
                    8),
                new SemanticGatewayStaticRoute(
                    routeB.RealmId,
                    routeB.MapId,
                    routeB.WorldInstanceId,
                    IntegrationNodeB,
                    8)
            ],
            maximumAdmissions: 16);
        var mapRoutes =
            new Dictionary<MapId, SemanticGatewayRouteTarget>
            {
                [routeA.MapId] = routeA,
                [routeB.MapId] = routeB
            };
        var workers =
            new Dictionary<ServerNodeId, SemanticGatewayWorkerTarget>
            {
                [IntegrationNodeA] = new(
                    IntegrationNodeA,
                    endpointA,
                    "integration-worker-a",
                    Pins(workerCertificateA)),
                [IntegrationNodeB] = new(
                    IntegrationNodeB,
                    endpointB,
                    "integration-worker-b",
                    Pins(workerCertificateB))
            };
        return new SemanticGatewayRuntimeConfiguration(
            new IPEndPoint(
                IPAddress.Loopback,
                ReserveLoopbackPort()),
            new IPEndPoint(
                IPAddress.Loopback,
                ReserveLoopbackPort()),
            IPAddress.Loopback.ToString(),
            ReserveLoopbackPort(),
            gatewayCertificate,
            new SemanticGatewayClientRuntimeLimits(
                listenBacklog: 16,
                maximumConnections: 32,
                maximumUnauthenticatedConnections: 16,
                maximumUnauthenticatedConnectionsPerIp: 16,
                maximumUnauthenticatedConnectionsPerPrefix: 16,
                bufferSizeBytes: 4 * 1024,
                firstPacketTimeout: TimeSpan.FromSeconds(3),
                idleTimeout: TimeSpan.FromSeconds(20),
                gracefulDrainTimeout: TimeSpan.FromSeconds(2)),
            BackhaulRuntimeLimits.Default,
            maximumConcurrentBackhaulTlsHandshakes: 8,
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 16,
                maximumAdmissions: 16,
                maximumAdmissionsPerGeneration: 1,
                loginGenerationTtl: TimeSpan.FromSeconds(30),
                reservationTtl: TimeSpan.FromSeconds(2),
                committedAdmissionTtl: TimeSpan.FromSeconds(2)),
            routes,
            routeA,
            mapRoutes,
            workers);
    }

    private static BackhaulCertificatePins Pins(
        X509Certificate2 certificate) =>
        new(
            [BackhaulCertificatePins.FingerprintOf(certificate)]);

    private static SemanticGatewayRouteTarget IntegrationRouteA() =>
        new(
            RealmId.Tempest,
            IntegrationMapA,
            IntegrationWorldA);

    private static SemanticGatewayRouteTarget IntegrationRouteB() =>
        new(
            RealmId.Tempest,
            IntegrationMapB,
            IntegrationWorldB);

    private static async Task<SemanticGatewayLoginGenerationLease>
        BeginHostLoginAsync(
        ISemanticGatewayCoordination coordination,
        int accountId,
        string username,
        CancellationToken cancellationToken)
    {
        var deadline =
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(5),
                TimeProvider.System);
        var result = await coordination.StartLoginAsync(
            new SemanticGatewayPrincipal(
                accountId,
                username),
            new SemanticGatewayConnectionSource(
                GatewayConnectionId.New(),
                IPAddress.Loopback),
            deadline,
            cancellationToken);
        Check.True(
            result.IsStarted && result.Generation is not null,
            $"direct authenticated login hook starts {username}");
        Check.True(
            await coordination.ActivateLoginAsync(
                result.Generation!,
                CoordinationDeadline.FromNow(
                    TimeSpan.FromSeconds(5),
                    TimeProvider.System),
                cancellationToken),
            $"direct authenticated login hook activates {username}");
        return result.Generation!;
    }

    private static ISemanticGatewayCoordination HostCoordination(
        SemanticGatewayHost host) =>
        typeof(SemanticGatewayHost).GetField(
            "_coordination",
            BindingFlags.Instance | BindingFlags.NonPublic)?
        .GetValue(host) as ISemanticGatewayCoordination
        ?? throw new InvalidOperationException(
            "Semantic gateway host coordination field was not found.");

    private static byte[] EncryptedGameLogin(string username)
    {
        var packet = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.LoginGameServer);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, 32),
            username);
        new PacketCipher().Transform(packet);
        return packet;
    }

    private static async Task<TcpClient> OpenGameAsync(
        IPEndPoint endpoint,
        byte[] encryptedLogin,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(
                endpoint.Address,
                endpoint.Port,
                cancellationToken);
            await client.GetStream().WriteAsync(
                encryptedLogin,
                cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(
        TcpClient client,
        int count,
        CancellationToken cancellationToken)
    {
        var value = new byte[count];
        await client.GetStream().ReadExactlyAsync(
            value,
            cancellationToken);
        return value;
    }

    private static async Task ExpectClosedAsync(
        TcpClient client,
        CancellationToken cancellationToken,
        string description)
    {
        var buffer = new byte[1];
        var read = await client.GetStream().ReadAsync(
            buffer,
            cancellationToken);
        Check.Equal(0, read, description);
    }

    private static async Task AssertRelayAliveAsync(
        TcpClient client,
        CancellationToken cancellationToken,
        string description)
    {
        var payload = new byte[] { 0xC1, 0xC2, 0xC3 };
        await client.GetStream().WriteAsync(
            payload,
            cancellationToken);
        var echoed = await ReadExactlyAsync(
            client,
            payload.Length,
            cancellationToken);
        Check.True(echoed.SequenceEqual(payload), description);
    }

    private static void CheckWorkerRoute(
        CapturedBackhaulSession session,
        ServerNodeId node,
        MapId map,
        WorldInstanceId world,
        byte[] encryptedLogin,
        string description)
    {
        Check.True(
            session.Admission.TargetNodeId == node &&
            session.Admission.RealmId == RealmId.Tempest &&
            session.Admission.MapId == map &&
            session.Admission.WorldInstanceId == world,
            $"{description} preserves the exact route identity");
        Check.True(
            session.EncryptedLogin.SequenceEqual(encryptedLogin),
            $"{description} preserves untouched legacy ciphertext");
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task IgnoreHostFailureAsync(Task hostRun)
    {
        try
        {
            await hostRun;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class SemanticGatewayHostData(
        IReadOnlyDictionary<int, byte> maps) :
        ISemanticGatewayDataSession
    {
        public Task<SemanticGatewayAuthenticatedAccount?>
            AuthenticateAsync(
                string username,
                ReadOnlyMemory<byte> password,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = password;
            var accountId = username switch
            {
                "test2" => 7,
                "test13" => 13,
                _ => 0
            };
            return Task.FromResult(
                accountId > 0
                    ? new SemanticGatewayAuthenticatedAccount(
                        accountId,
                        username)
                    : null);
        }

        public Task<SemanticGatewayCharacterRoute?>
            FindCharacterRouteAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                maps.TryGetValue(accountId, out var map)
                    ? new SemanticGatewayCharacterRoute(
                        checked(accountId * 10),
                        MapId.FromLegacy(map))
                    : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
