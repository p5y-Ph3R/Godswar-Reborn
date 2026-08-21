using System.Net;
using System.Net.Sockets;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    public const string HostIntegrationCheckName =
        "B18C2 semantic gateway real-socket host integration";

    private static readonly ServerNodeId IntegrationNodeA =
        new("integration-worker-a");
    private static readonly ServerNodeId IntegrationNodeB =
        new("integration-worker-b");
    private static readonly MapId IntegrationMapA = new(4);
    private static readonly MapId IntegrationMapB = new(5);
    private static readonly WorldInstanceId IntegrationWorldA =
        new(Guid.Parse("aaaaaaaa-1000-4000-8000-000000000001"));
    private static readonly WorldInstanceId IntegrationWorldB =
        new(Guid.Parse("bbbbbbbb-1000-4000-8000-000000000002"));

    public static async Task RunHostIntegrationAsync()
    {
        using var gatewayCertificate = CreateCertificate(
            "integration-gateway",
            ClientAuthenticationOid);
        using var workerCertificateA = CreateCertificate(
            "integration-worker-a",
            ServerAuthenticationOid);
        using var workerCertificateB = CreateCertificate(
            "integration-worker-b",
            ServerAuthenticationOid);
        await using var workerA =
            await LoopbackBackhaulWorker.StartAsync(
                IntegrationNodeA,
                IntegrationRouteA(),
                workerCertificateA,
                gatewayCertificate,
                marker: [0xA1, 0xA2]);
        await using var workerB =
            await LoopbackBackhaulWorker.StartAsync(
                IntegrationNodeB,
                IntegrationRouteB(),
                workerCertificateB,
                gatewayCertificate,
                marker: [0xB1, 0xB2]);

        await using var data = new SemanticGatewayHostData(
            new Dictionary<int, byte>
            {
                [7] = checked((byte)IntegrationMapA.Value),
                [8] = checked((byte)IntegrationMapA.Value),
                [9] = checked((byte)IntegrationMapA.Value),
                [13] = checked((byte)IntegrationMapB.Value)
            });
        using var configuration = CreateHostConfiguration(
            gatewayCertificate,
            workerCertificateA,
            workerCertificateB,
            workerA.Endpoint,
            workerB.Endpoint);
        await using var host = new SemanticGatewayHost(
            configuration,
            data);
        using var stop = new CancellationTokenSource(
            TimeSpan.FromSeconds(45));
        var hostRun = host.RunAsync(stop.Token);
        try
        {
            var endpoints = await host.WaitUntilStartedAsync(
                stop.Token);
            var coordination = HostCoordination(host);
            var generationA = await BeginHostLoginAsync(
                coordination,
                7,
                "test2",
                SemanticGatewayTestRealm.TempestGrant,
                stop.Token);
            using (var wrongToken = await OpenGameAsync(
                       endpoints.Game,
                       EncryptedGameLogin(
                           "test2",
                           SemanticGatewayTestRealm.TempestGrant,
                           identifierOverride:
                               SemanticGatewayTestRealm
                                   .DwargonGrant.Identifier),
                       stop.Token))
            {
                await ExpectClosedAsync(
                    wrongToken,
                    stop.Token,
                    "selected realm rejects a forged game-login token");
            }

            _ = await BeginHostLoginAsync(
                coordination,
                13,
                "test13",
                SemanticGatewayTestRealm.TempestGrant,
                stop.Token);
            using (var wrongRealm = await OpenGameAsync(
                       endpoints.Game,
                       EncryptedGameLogin(
                           "test13",
                           SemanticGatewayTestRealm.DwargonGrant),
                       stop.Token))
            {
                await ExpectClosedAsync(
                    wrongRealm,
                    stop.Token,
                    "game-login realm cannot differ from login selection");
            }
            Check.True(
                workerA.SessionCount == 0 && workerB.SessionCount == 0,
                "realm and token forgeries never reach a worker");

            _ = await BeginHostLoginAsync(
                coordination,
                13,
                "test13",
                SemanticGatewayTestRealm.DwargonGrant,
                stop.Token);
            var encryptedA = EncryptedGameLogin(
                "test2",
                SemanticGatewayTestRealm.TempestGrant);
            var encryptedB = EncryptedGameLogin(
                "test13",
                SemanticGatewayTestRealm.DwargonGrant);

            data.SetEnabledRealms(
                new RealmCatalogSnapshot(
                    [SemanticGatewayTestRealm.Tempest]));
            using (var disabledRealm = await OpenGameAsync(
                       endpoints.Game,
                       encryptedB,
                       stop.Token))
            {
                await ExpectClosedAsync(
                    disabledRealm,
                    stop.Token,
                    "game login rechecks that selected realm remains enabled");
            }
            data.SetEnabledRealms(SemanticGatewayTestRealm.Catalog);
            Check.True(
                workerA.SessionCount == 0 && workerB.SessionCount == 0,
                "disabled realm rejection never reaches a worker");

            using var clientA = await OpenGameAsync(
                endpoints.Game,
                encryptedA,
                stop.Token);
            using var clientB = await OpenGameAsync(
                endpoints.Game,
                encryptedB,
                stop.Token);
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                stop.Token);
            var initialSnapshot = host.GameSnapshot;
            Check.True(
                initialSnapshot.ActiveConnections == 2 ||
                workerA.SessionCount != 0 &&
                workerB.SessionCount != 0,
                $"initial gateway sessions remain active: {initialSnapshot}; " +
                $"workerA={workerA.SessionCount}; " +
                $"workerB={workerB.SessionCount}");
            var sessionA = await workerA.WaitForSessionAsync(
                1,
                stop.Token);
            var sessionB = await workerB.WaitForSessionAsync(
                1,
                stop.Token);
            CheckWorkerRoute(
                sessionA,
                RealmId.Tempest,
                IntegrationNodeA,
                IntegrationMapA,
                IntegrationWorldA,
                encryptedA,
                "first exact route");
            CheckWorkerRoute(
                sessionB,
                RealmId.Dwargon,
                IntegrationNodeB,
                IntegrationMapB,
                IntegrationWorldB,
                encryptedB,
                "second exact route");
            Check.True(
                (await ReadExactlyAsync(
                    clientA,
                    2,
                    stop.Token)).SequenceEqual(
                        new byte[] { 0xA1, 0xA2 }) &&
                (await ReadExactlyAsync(
                    clientB,
                    2,
                    stop.Token)).SequenceEqual(
                        new byte[] { 0xB1, 0xB2 }),
                "worker-specific responses traverse the real gateway relay");

            using (var replay = await OpenGameAsync(
                       endpoints.Game,
                       encryptedA,
                       stop.Token))
            {
                await ExpectClosedAsync(
                    replay,
                    stop.Token,
                    "same login generation is rejected while active");
            }
            Check.Equal(
                1,
                workerA.SessionCount,
                "replay never reaches worker A");

            await CheckRouteLifecycleIsolationAsync(
                configuration,
                coordination,
                endpoints.Game,
                workerA,
                clientA,
                clientB,
                stop.Token);

            using (var staleReconnect = await OpenGameAsync(
                       endpoints.Game,
                       encryptedA,
                       stop.Token))
            {
                await ExpectClosedAsync(
                    staleReconnect,
                    stop.Token,
                    "lost session cannot reuse its consumed generation");
            }
            Check.Equal(
                1,
                workerA.SessionCount,
                "stale generation does not reopen worker A");

            var freshGeneration = await BeginHostLoginAsync(
                coordination,
                7,
                "test2",
                SemanticGatewayTestRealm.TempestGrant,
                stop.Token);
            Check.True(
                freshGeneration.GenerationId !=
                    generationA.GenerationId,
                "full login creates a fresh reconnect generation");
            using var reconnected = await OpenGameAsync(
                endpoints.Game,
                encryptedA,
                stop.Token);
            var replacement = await workerA.WaitForSessionAsync(
                2,
                stop.Token);
            CheckWorkerRoute(
                replacement,
                RealmId.Tempest,
                IntegrationNodeA,
                IntegrationMapA,
                IntegrationWorldA,
                encryptedA,
                "fresh reconnect route");
            Check.True(
                (await ReadExactlyAsync(
                    reconnected,
                    2,
                    stop.Token)).SequenceEqual(
                        new byte[] { 0xA1, 0xA2 }),
                "fresh full-login generation reconnects through mTLS");

            workerA.DropActiveSessions();
            await ExpectClosedAsync(
                reconnected,
                stop.Token,
                "worker A loss closes only its gateway client");
            await AssertRelayAliveAsync(
                clientB,
                stop.Token,
                "worker B remains connected after worker A loss");
            using (var consumedReconnect = await OpenGameAsync(
                       endpoints.Game,
                       encryptedA,
                       stop.Token))
            {
                await ExpectClosedAsync(
                    consumedReconnect,
                    stop.Token,
                    "worker loss cannot reuse its consumed generation");
            }
            Check.Equal(
                2,
                workerA.SessionCount,
                "worker loss does not reopen from the consumed generation");

            var finalGeneration = await BeginHostLoginAsync(
                coordination,
                7,
                "test2",
                SemanticGatewayTestRealm.TempestGrant,
                stop.Token);
            Check.True(
                finalGeneration.GenerationId !=
                    freshGeneration.GenerationId,
                "worker-loss reconnect requires another full login");
            using var finalReconnect = await OpenGameAsync(
                endpoints.Game,
                encryptedA,
                stop.Token);
            _ = await workerA.WaitForSessionAsync(
                3,
                stop.Token);
            Check.True(
                (await ReadExactlyAsync(
                    finalReconnect,
                    2,
                    stop.Token)).SequenceEqual(
                        new byte[] { 0xA1, 0xA2 }),
                "fresh generation reconnects after worker recovery");

            workerA.DelayNextRelease(
                TimeSpan.FromMilliseconds(250));
            var replacementGeneration = await BeginHostLoginAsync(
                coordination,
                7,
                "test2",
                SemanticGatewayTestRealm.TempestGrant,
                stop.Token);
            Check.True(
                replacementGeneration.GenerationId !=
                    finalGeneration.GenerationId,
                "new authenticated login supersedes the active generation");
            using var replacementClient = await OpenGameAsync(
                endpoints.Game,
                encryptedA,
                stop.Token);
            Check.True(
                await workerA.WaitForRejectionAsync(stop.Token) ==
                    BackhaulAdmissionStatus.AccountAlreadyActive,
                "replacement observes delayed remote worker release");
            await ExpectClosedAsync(
                finalReconnect,
                stop.Token,
                "replacement game admission closes the prior relay");
            _ = await workerA.WaitForSessionAsync(
                4,
                stop.Token);
            Check.True(
                (await ReadExactlyAsync(
                    replacementClient,
                    2,
                    stop.Token)).SequenceEqual(
                        new byte[] { 0xA1, 0xA2 }),
                "replacement waits for old worker ownership then connects");
        }
        finally
        {
            stop.Cancel();
            await IgnoreHostFailureAsync(hostRun);
        }
    }

    private static async Task CheckRouteLifecycleIsolationAsync(
        SemanticGatewayRuntimeConfiguration configuration,
        ISemanticGatewayCoordination coordination,
        IPEndPoint gameEndpoint,
        LoopbackBackhaulWorker workerA,
        TcpClient clientA,
        TcpClient clientB,
        CancellationToken cancellationToken)
    {
        var draining = configuration.RouteDirectory.UpdateWorkerState(
            IntegrationNodeA,
            expectedRevision: 1,
            SemanticGatewayWorkerState.Draining);
        Check.True(
            draining.Status ==
                SemanticGatewayWorkerUpdateStatus.Updated,
            "worker A enters drain at its exact revision");
        _ = await BeginHostLoginAsync(
            coordination,
            8,
            "DRAINED",
            SemanticGatewayTestRealm.TempestGrant,
            cancellationToken);
        using (var rejected = await OpenGameAsync(
                   gameEndpoint,
                   EncryptedGameLogin(
                       "DRAINED",
                       SemanticGatewayTestRealm.TempestGrant),
                   cancellationToken))
        {
            await ExpectClosedAsync(
                rejected,
                cancellationToken,
                "draining route fails closed");
        }
        Check.Equal(
            1,
            workerA.SessionCount,
            "draining route rejection never opens another worker session");
        await Task.Delay(
            TimeSpan.FromMilliseconds(1_250),
            cancellationToken);
        await AssertRelayAliveAsync(
            clientA,
            cancellationToken,
            "committed worker A session refreshes while draining");
        await AssertRelayAliveAsync(
            clientB,
            cancellationToken,
            "worker B survives worker A drain");

        var unavailable =
            configuration.RouteDirectory.UpdateWorkerState(
                IntegrationNodeA,
                expectedRevision: 2,
                SemanticGatewayWorkerState.Unavailable);
        Check.True(
            unavailable.Status ==
                SemanticGatewayWorkerUpdateStatus.Updated,
            "worker A transitions from draining to unavailable");
        _ = await BeginHostLoginAsync(
            coordination,
            9,
            "OFFLINE",
            SemanticGatewayTestRealm.TempestGrant,
            cancellationToken);
        using (var rejected = await OpenGameAsync(
                   gameEndpoint,
                   EncryptedGameLogin(
                       "OFFLINE",
                       SemanticGatewayTestRealm.TempestGrant),
                   cancellationToken))
        {
            await ExpectClosedAsync(
                rejected,
                cancellationToken,
                "unavailable route fails closed");
        }
        await ExpectClosedAsync(
            clientA,
            cancellationToken,
            "committed session closes when refresh sees unavailable");
        await AssertRelayAliveAsync(
            clientB,
            cancellationToken,
            "worker B survives worker A unavailable state");
        Check.True(
            coordination.GetSnapshot().RouteRejections >= 2,
            "gateway records both exact route lifecycle rejections");

        var restored =
            configuration.RouteDirectory.UpdateWorkerState(
                IntegrationNodeA,
                expectedRevision: 3,
                SemanticGatewayWorkerState.Available);
        Check.True(
            restored.Status ==
                SemanticGatewayWorkerUpdateStatus.Updated &&
            restored.Worker!.Revision == 4,
            "worker A returns with a new authoritative revision");
    }

}
