using Godswar.Server;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Networking;
using Godswar.Server.Networking.RelayGateway;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

using var controlledHostEvidence =
    ControlledHostPrivacyEvidence.TryInstallFromEnvironment();

if (await ServerStartupCommandDispatcher.TryRunAsync(args))
{
    return;
}

var optionsPath = args.Length > 0 ? args[0] : "appsettings.json";
if (!ServerRuntimeBootstrap.TryLoadOptions(
        optionsPath,
        out var options,
        out var runtimeProfile))
{
    return;
}

using var observability = ServerObservabilityRuntime.Start(
    options.Operations.Management.MaximumResponseBytes,
    installConsoleBoundary: controlledHostEvidence is null);
LegacyPersistenceMetrics.EnsureInitialized();
observability.RecordLifecycle("server", "starting");

try
{
    var legacyAuthenticationAccess =
        LegacyAuthenticationAccess.Create(runtimeProfile);
    var phase4AcceptanceFaults =
        SecurePhase4AcceptanceFaults.Create(
            options.Secure.Phase4AcceptanceFaults);
    JsonGameStore? jsonGameStore = null;
    await using IGameStore store = runtimeProfile.StorageProvider switch
    {
        GameStorageProviderKind.Postgres =>
            new PostgresGameStore(
                options.Storage.PostgresConnectionString),
        GameStorageProviderKind.Json =>
            jsonGameStore = new JsonGameStore(options.DataPath),
        _ => throw new InvalidOperationException(
            "Validated storage provider is not exhaustive.")
    };
    LegacyPersistenceMetrics.Record(
        LegacyPersistenceOperation.EnsureSeedData);
    await store.EnsureSeedDataAsync();
    await using PostgresApplicationDataRuntime?
        postgresApplicationDataRuntime =
            runtimeProfile.StorageProvider == GameStorageProviderKind.Postgres
                ? new PostgresApplicationDataRuntime(
                    options.Storage.PostgresConnectionString,
                    options.Storage.Outbox,
                    options.Game.ZodiacEnergy.Snapshot(),
                    options.Storage.Reconciliation)
                : null;
    var accountPersistence = ServerAccountPersistenceComposition.Create(
        postgresApplicationDataRuntime,
        jsonGameStore);
    ICharacterSnapshotReader characterSnapshotReader =
        runtimeProfile.StorageProvider switch
        {
            GameStorageProviderKind.Postgres =>
                postgresApplicationDataRuntime?.CharacterSnapshots ??
                throw new InvalidOperationException(
                    "PostgreSQL character snapshot reader was not composed."),
            GameStorageProviderKind.Json =>
                jsonGameStore ??
                throw new InvalidOperationException(
                    "JSON character snapshot reader was not composed."),
            _ => throw new InvalidOperationException(
                "Validated storage provider has no character snapshot reader.")
        };
    var measuredCharacterSnapshots =
        new MeasuredCharacterSnapshotReader(
            characterSnapshotReader,
            runtimeProfile.StorageProvider == GameStorageProviderKind.Postgres
                ? CharacterSnapshotProvider.PostgreSql
                : CharacterSnapshotProvider.Json);
    var worldContent =
        await ServerWorldContentComposition.TryLoadAsync(
            runtimeProfile,
            options);
    if (worldContent is null)
    {
        return;
    }

    using var shutdown = new CancellationTokenSource();
    var controlledHostShutdown =
        ControlledHostShutdownControl.TryCreateFromEnvironment(
            options,
            controlledHostEvidence is not null,
            shutdown);
    await using var coordination =
        await ServerCoordinationComposition.CreateAsync(
            options,
            worldContent.Manifest.Revision,
            shutdown.Token);

    ICharacterCheckpointStore characterCheckpointStore =
        postgresApplicationDataRuntime?.CharacterCheckpoints ??
        new LegacyCharacterCheckpointStore(store);
    await using var characterCheckpoints =
        new CharacterCheckpointCoordinator(
            characterCheckpointStore,
            options.Storage.Checkpoints);
    var registry = new GameSessionRegistry(
        store,
        options.Game.ZodiacEnergy,
        options.Game.Monsters.Runtime,
        options.Game.Players.Runtime,
        characterCheckpoints,
        postgresApplicationDataRuntime?
            .ProgressionIntervalSettlementCommands,
        requiresDurablePlayerPersistence:
            postgresApplicationDataRuntime is not null,
        worldInstanceOptions:
            options.Game.WorldInstances);
    var gameHandlerFactory = new GameClientHandlerFactory(
        store,
        accountPersistence.Directory,
        accountPersistence.Presence,
        registry,
        measuredCharacterSnapshots,
        worldContent,
        options.Game.DeveloperCommands,
        characterCheckpoints,
        postgresApplicationDataRuntime,
        coordination.Worker);
    var admission = new ConnectionAdmission(new ConnectionAdmissionOptions(
        options.Network.MaxActiveConnections,
        options.Network.MaxUnauthenticatedConnections,
        options.Network.MaxUnauthenticatedConnectionsPerIp,
        options.Network.MaxUnauthenticatedConnectionsPerPrefix));
    using var workerBackhaulRuntime =
        WorkerBackhaulRuntime.TryCreate(
            options,
            admission,
            gameHandlerFactory);
    var listenerProfile = workerBackhaulRuntime is null
        ? ServerListenerProfile.Build(options)
        : null;
    var (loginServer, gameServer) =
        ServerRuntimeListenerFactory.CreateRawPair(
            listenerProfile,
            options,
            admission,
            session => new LoginClientHandler(
                session,
                accountPersistence.LegacyLogin,
                options,
                legacyAuthenticationAccess:
                    legacyAuthenticationAccess),
            session => gameHandlerFactory.Create(
                session,
                legacyAuthenticationAccess:
                    legacyAuthenticationAccess));

    using SecureServerCertificate? secureCertificate =
        options.Secure.Enabled
            ? SecureServerCertificate.Load(options.Secure)
            : null;
    var secureGameTarget = options.Secure.Enabled
        ? options.Secure.BuildGameTarget()
        : null;
    await using SecureUdpRuntime? secureUdpRuntime =
        secureGameTarget is not null
            ? SecureUdpRuntime.TryCreate(
                options.Secure,
                secureGameTarget,
                SecureUdpRuntimeCapabilities.Current,
                phase4AcceptanceFaults:
                    phase4AcceptanceFaults)
            : null;
    IGameTicketStore? secureGameTickets =
        options.Secure.Enabled
            ? coordination.CreateGameTicketStore(
                options.Secure.Tickets)
            : null;
    using var operationalStateMetrics = new OperationalStateMetrics(
        admission,
        secureGameTickets as IGameTicketStoreSnapshotSource,
        secureUdpRuntime,
        coordination.Worker);
    var serverOperationalState = new ServerOperationalState(
        ServerReadinessDependency.All);
    serverOperationalState.SetDependency(
        ServerReadinessDependency.SchemaAndContent,
        ready: true);
    var drainCoordinator = new ServerDrainCoordinator(
        serverOperationalState,
        admission,
        shutdown,
        options.Network.GracefulDrainTimeout);
    ManagementDrainResult BeginRuntimeDrain()
    {
        workerBackhaulRuntime?.BeginDrain();
        coordination.Worker?.BeginDrain();
        return drainCoordinator.BeginDrain();
    }

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        BeginRuntimeDrain();
    };
    using var processSignals =
        ServerProcessSignalRegistration.Install(
            () => BeginRuntimeDrain());
    ManagementTokenAuthenticator? loadedManagementToken = null;
    try
    {
        if (options.Operations.Management.Enabled)
        {
            loadedManagementToken =
                ManagementDrainTokenFile.TryLoad(
                    options.Operations.DrainTokenFile);
        }
    }
    catch
    {
        observability.RecordLifecycle(
            "management",
            "configuration_rejected",
            OperationalLogLevel.Error);
        Environment.ExitCode = 2;
        return;
    }
    using var managementToken = loadedManagementToken;
    var taskSupervisor = new CriticalTaskSupervisor(
        serverOperationalState,
        shutdown.Cancel,
        observability.RecordCriticalTask);
    using var serverOperationsMetrics =
        new ServerOperationsMetrics(
            serverOperationalState,
            taskSupervisor,
            observability.RecordManagement);
    var managementServer = options.Operations.Management.Enabled
        ? new ManagementHttpServer(
            options.Operations.Management,
            serverOperationalState.GetSnapshot,
            observability.GetMetricsAsync,
            observability.GetTracesAsync,
            token => managementToken?.Authenticate(token) == true,
            _ => ValueTask.FromResult(
                BeginRuntimeDrain()),
            serverOperationsMetrics.RecordManagement)
        : null;
    var readinessMonitor = new ServerReadinessMonitor(
        serverOperationalState,
        options.Operations.Readiness,
        characterCheckpoints,
        registry,
        postgresApplicationDataRuntime,
        postgresApplicationDataRuntime?.OutboxEnabled == true,
        options.Game.ZodiacEnergy.Enabled,
        secureUdpRuntime,
        coordination.Worker,
        observability.RecordOperationalState);
    using var progressionRetryMetrics =
        new DurableProgressionRetryMetrics(
            registry.GetDurableProgressionRetrySnapshot);
    await using AccountAuthenticationService? secureAuthentication =
        options.Secure.Enabled
            ? new AccountAuthenticationService(
                accountPersistence.Credentials,
                accountPersistence.Presence,
                options.Authentication)
            : null;
    using TlsHandshakeGate? secureHandshakeGate =
        options.Secure.Enabled
            ? new TlsHandshakeGate(options.Network.MaxConcurrentTlsHandshakes)
            : null;
    var secureTransportFactory =
        secureCertificate is not null && secureHandshakeGate is not null
            ? new TlsMuxLegacyTransportFactory(
                options.Secure,
                options.Network,
                secureCertificate.Context,
                secureHandshakeGate,
                ticketStore: secureGameTickets,
                gameTarget: secureGameTarget,
                udpSessionAuthority: secureUdpRuntime?.Authority)
            : null;
    var secureLoginServer = secureTransportFactory is null
        ? null
        : new TcpEndpointServer(
            NetworkEndpointRole.Login,
            listenerProfile!.Login.Host,
            listenerProfile.Login.Port,
            options.Network,
            admission,
            session => new LoginClientHandler(
                session,
                accountPersistence.LegacyLogin,
                options,
                secureAuthentication,
                secureGameTickets,
                secureGameTarget),
            transportFactory: secureTransportFactory);
    var secureGameServer = secureTransportFactory is null
        ? null
        : new TcpEndpointServer(
            NetworkEndpointRole.Game,
            listenerProfile!.Game.Host,
            listenerProfile.Game.Port,
            options.Network,
            admission,
            session => gameHandlerFactory.Create(
                session,
                phase4AcceptanceFaults),
            transportFactory: secureTransportFactory);

    var endpointServers = new List<TcpEndpointServer>(2);
    var criticalTasks = new CriticalTaskCollection(
        taskSupervisor,
        shutdown.Token);
    var auxiliaryTasks = new List<Task>(1);
    Task? checkpointTask = null;
    var fatalRuntimeFailure = false;

    try
    {
        checkpointTask = taskSupervisor.RunAsync(
            CriticalTaskKind.CheckpointWorker,
            _ => characterCheckpoints.RunAsync(),
            shutdown.Token);
        await characterCheckpoints.WaitUntilReadyAsync(
            shutdown.Token).WaitAsync(
                TimeSpan.FromSeconds(10),
                shutdown.Token);
        if (coordination.Worker is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.RedisCoordination,
                coordination.Worker.RunAsync);
            await coordination.Worker.WaitUntilRegisteredAsync(
                shutdown.Token).WaitAsync(
                    TimeSpan.FromSeconds(10),
                    shutdown.Token);
        }

        criticalTasks.Start(
            CriticalTaskKind.MonsterWorld,
            registry.RunMonsterRoamingAsync);
        criticalTasks.Start(
            CriticalTaskKind.PlayerRecovery,
            registry.RunPlayerRecoveryAsync);
        criticalTasks.Start(
            CriticalTaskKind.ExperienceBoostReconciliation,
            registry.RunExperienceBoostStatusReconciliationAsync);
        if (options.Game.ZodiacEnergy.Enabled)
        {
            criticalTasks.Start(
                CriticalTaskKind.ZodiacEnergyAccrual,
                registry.RunZodiacEnergyAccrualAsync);
        }
        if (postgresApplicationDataRuntime is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.DurableProgressionRetry,
                registry.RunDurableProgressionRetryAsync);
        }
        if (postgresApplicationDataRuntime?.OutboxEnabled == true)
        {
            criticalTasks.Start(
                CriticalTaskKind.OutboxDispatcher,
                postgresApplicationDataRuntime.RunOutboxAsync);
        }
        ServerReconciliationComposition.StartWorkerIfEnabled(
            criticalTasks,
            postgresApplicationDataRuntime);

        if (secureUdpRuntime is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.SecureUdp,
                secureUdpRuntime.RunAsync);
        }

        if (loginServer is not null && gameServer is not null)
        {
            endpointServers.Add(loginServer);
            endpointServers.Add(gameServer);
            criticalTasks.Start(
                CriticalTaskKind.LoginListener,
                loginServer.RunAsync);
            criticalTasks.Start(
                CriticalTaskKind.GameListener,
                gameServer.RunAsync);
        }
        if (secureLoginServer is not null && secureGameServer is not null)
        {
            endpointServers.Add(secureLoginServer);
            endpointServers.Add(secureGameServer);
            criticalTasks.Start(
                CriticalTaskKind.LoginListener,
                secureLoginServer.RunAsync);
            criticalTasks.Start(
                CriticalTaskKind.GameListener,
                secureGameServer.RunAsync);
        }
        if (workerBackhaulRuntime is not null)
        {
            workerBackhaulRuntime.Start(
                endpointServers,
                criticalTasks);
        }

        WorkerBackhaulRuntime.ValidateListenerComposition(
            workerBackhaulRuntime,
            endpointServers.Count);

        if (managementServer is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.ManagementHttp,
                managementServer.RunAsync);
        }
        criticalTasks.Start(
            CriticalTaskKind.PostgresReadiness,
            readinessMonitor.RunAsync);
        taskSupervisor.SealRegistrations();

        await Task.WhenAll(endpointServers.Select(
            server => server.WaitUntilStartedAsync(shutdown.Token))).WaitAsync(
            TimeSpan.FromSeconds(10),
            shutdown.Token);
        if (managementServer is not null)
        {
            await managementServer.WaitUntilStartedAsync(
                shutdown.Token).WaitAsync(
                    TimeSpan.FromSeconds(10),
                    shutdown.Token);
        }
        if (secureUdpRuntime is not null)
        {
            await secureUdpRuntime.WaitUntilReadyAsync(
                shutdown.Token).WaitAsync(
                    TimeSpan.FromSeconds(10),
                    shutdown.Token);
        }
        await readinessMonitor.WaitUntilFirstRefreshAsync(
            shutdown.Token).WaitAsync(
                TimeSpan.FromSeconds(10),
                shutdown.Token);
        serverOperationalState.SetDependency(
            ServerReadinessDependency.ListenerProfile,
            ready: true);
        if (coordination.Worker is not null)
        {
            await coordination.Worker.PublishAvailableAsync(
                shutdown.Token);
            await coordination.Worker.WaitUntilReadyAsync(
                shutdown.Token).WaitAsync(
                    TimeSpan.FromSeconds(10),
                    shutdown.Token);
        }
        await readinessMonitor.RefreshAsync(shutdown.Token);
        serverOperationalState.TryMarkRunning();
        ControlledHostPrivacyEvidence.RecordIfActive(
            ControlledHostEvidenceEvent.SecureListenersReady);
        observability.RecordLifecycle(
            "server",
            serverOperationalState.GetSnapshot().IsReady
                ? "ready"
                : "running_not_ready");

        if (controlledHostShutdown is not null)
        {
            var controlTask =
                controlledHostShutdown.RunAsync(shutdown.Token);
            auxiliaryTasks.Add(controlTask);
            _ = controlTask.ContinueWith(
                static (_, state) =>
                    ((CancellationTokenSource)state!).Cancel(),
                shutdown,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        await Task.WhenAll(
            criticalTasks.Items.Concat(auxiliaryTasks));
    }
    catch
    {
        fatalRuntimeFailure = true;
        serverOperationalState.MarkCriticalTaskFaulted();
        observability.RecordLifecycle(
            "server",
            "faulted",
            OperationalLogLevel.Critical);
    }
    finally
    {
        workerBackhaulRuntime?.BeginDrain();
        coordination.Worker?.BeginDrain();
        admission.BeginDrain();
        serverOperationalState.TryMarkStopping();
        var shutdownTasks = criticalTasks.Items
            .Concat(auxiliaryTasks)
            .Concat(
                checkpointTask is null
                    ? []
                    : [checkpointTask]);
        if (!await CriticalTaskShutdown.CompleteAsync(
                characterCheckpoints,
                shutdown,
                shutdownTasks,
                options.Storage.Checkpoints.ShutdownDrainTimeout))
        {
            fatalRuntimeFailure = true;
        }
        if (!await ServerRuntimeShutdown.TryDisposeWorldInstancesAsync(
                registry, observability))
        {
            fatalRuntimeFailure = true;
        }
        serverOperationalState.MarkStopped();
    }

    ServerRuntimeShutdown.SetProcessOutcome(fatalRuntimeFailure, observability);
}
catch
{
    observability.RecordLifecycle(
        "server",
        "startup_failed",
        OperationalLogLevel.Critical);
    Environment.ExitCode = 4;
}
