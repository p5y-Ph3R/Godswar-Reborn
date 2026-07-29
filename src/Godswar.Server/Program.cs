using Godswar.Server;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

using var controlledHostEvidence =
    ControlledHostPrivacyEvidence.TryInstallFromEnvironment();

if (await ControlledHostValidationCommand.TryRunAsync(args))
{
    return;
}

var optionsPath = args.Length > 0 ? args[0] : "appsettings.json";
ServerOptions options;
ValidatedServerRuntimeProfile runtimeProfile;
try
{
    options = ServerOptions.Load(optionsPath);
    runtimeProfile = ServerRuntimeProfilePolicy.Validate(options);
}
catch (ServerStartupConfigurationException ex)
{
    var reason =
        ServerRuntimeProfilePolicy.RejectionCode(ex.Reason);
    ServerProfileMetrics.RecordStartupRejection(reason);
    Console.Error.WriteLine(
        $"[startup] rejected reason={reason}");
    Environment.ExitCode = 2;
    return;
}
catch (Exception)
{
    const string reason = "invalid_configuration";
    ServerProfileMetrics.RecordStartupRejection(reason);
    Console.Error.WriteLine(
        $"[startup] rejected reason={reason}");
    Environment.ExitCode = 2;
    return;
}

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
await store.EnsureSeedDataAsync();
await using PostgresApplicationDataRuntime?
    postgresApplicationDataRuntime =
        runtimeProfile.StorageProvider == GameStorageProviderKind.Postgres
            ? new PostgresApplicationDataRuntime(
                options.Storage.PostgresConnectionString,
                options.Storage.Outbox)
            : null;
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
IWorldContentReader worldContent;
try
{
    worldContent = runtimeProfile.StorageProvider switch
    {
        GameStorageProviderKind.Postgres =>
            await PostgresWorldContentBootstrapper.LoadAsync(
                options.Storage.PostgresConnectionString),
        GameStorageProviderKind.Json =>
            await GeneratedWorldContentReaderLoader.LoadAsync(),
        _ => throw new InvalidOperationException(
            "Validated storage provider has no world-content reader.")
    };
}
catch (WorldContentUnavailableException ex)
{
    Console.Error.WriteLine(
        "[world-content] startup rejected " +
        $"family={ex.Family} reason={ex.Reason}");
    Environment.ExitCode = 3;
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
var controlledHostShutdown =
    ControlledHostShutdownControl.TryCreateFromEnvironment(
        options,
        controlledHostEvidence is not null,
        shutdown);

var registry = new GameSessionRegistry(
    store,
    options.Game.ZodiacEnergy,
    options.Game.Monsters.Runtime,
    options.Game.Players.Runtime);
var admission = new ConnectionAdmission(new ConnectionAdmissionOptions(
    options.Network.MaxActiveConnections,
    options.Network.MaxUnauthenticatedConnections,
    options.Network.MaxUnauthenticatedConnectionsPerIp,
    options.Network.MaxUnauthenticatedConnectionsPerPrefix));
var listenerProfile = ServerListenerProfile.Build(options);
var rawCompatibilityEnabled =
    listenerProfile.Transport == ServerListenerTransport.RawTcp;
var loginServer = rawCompatibilityEnabled
    ? new TcpEndpointServer(
        NetworkEndpointRole.Login,
        listenerProfile.Login.Host,
        listenerProfile.Login.Port,
        options.Network,
        admission,
        session => new LoginClientHandler(
            session,
            store,
            options,
            legacyAuthenticationAccess:
                legacyAuthenticationAccess))
    : null;

var gameServer = rawCompatibilityEnabled
    ? new TcpEndpointServer(
        NetworkEndpointRole.Game,
        listenerProfile.Game.Host,
        listenerProfile.Game.Port,
        options.Network,
        admission,
        session => new GameClientHandler(
            session,
            store,
            registry,
            measuredCharacterSnapshots,
            worldContent,
            options.Game.DeveloperCommands,
            legacyAuthenticationAccess:
                legacyAuthenticationAccess,
            talentUpgradeCommands:
                postgresApplicationDataRuntime?
                    .TalentUpgradeCommands,
            developerItemGrantCommands:
                postgresApplicationDataRuntime?
                    .DeveloperItemGrantCommands,
            developerBagClearCommands:
                postgresApplicationDataRuntime?
                    .DeveloperBagClearCommands,
            makeAttributeStoneCommands:
                postgresApplicationDataRuntime?
                    .MakeAttributeStoneCommands,
            gearMentorMaterialConversionCommands:
                postgresApplicationDataRuntime?
                    .MaterialConversionCommands,
            gearMentorDecomposeGearCommands:
                postgresApplicationDataRuntime?
                    .DecomposeGearCommands))
    : null;

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
using InMemoryGameTicketStore? secureGameTickets =
    options.Secure.Enabled
        ? new InMemoryGameTicketStore(
            options.Secure.Tickets.Capacity,
            options.Secure.Tickets.Ttl)
        : null;
using var operationalStateMetrics = new OperationalStateMetrics(
    admission,
    secureGameTickets,
    secureUdpRuntime);
await using AccountAuthenticationService? secureAuthentication =
    options.Secure.Enabled
        ? new AccountAuthenticationService(
            store,
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
        listenerProfile.Login.Host,
        listenerProfile.Login.Port,
        options.Network,
        admission,
        session => new LoginClientHandler(
            session,
            store,
            options,
            secureAuthentication,
            secureGameTickets,
            secureGameTarget),
        transportFactory: secureTransportFactory);
var secureGameServer = secureTransportFactory is null
    ? null
    : new TcpEndpointServer(
        NetworkEndpointRole.Game,
        listenerProfile.Game.Host,
        listenerProfile.Game.Port,
        options.Network,
        admission,
        session => new GameClientHandler(
            session,
            store,
            registry,
            measuredCharacterSnapshots,
            worldContent,
            options.Game.DeveloperCommands,
            phase4AcceptanceFaults,
            talentUpgradeCommands:
                postgresApplicationDataRuntime?
                    .TalentUpgradeCommands,
            developerItemGrantCommands:
                postgresApplicationDataRuntime?
                    .DeveloperItemGrantCommands,
            developerBagClearCommands:
                postgresApplicationDataRuntime?
                    .DeveloperBagClearCommands,
            makeAttributeStoneCommands:
                postgresApplicationDataRuntime?
                    .MakeAttributeStoneCommands,
            gearMentorMaterialConversionCommands:
                postgresApplicationDataRuntime?
                    .MaterialConversionCommands,
            gearMentorDecomposeGearCommands:
                postgresApplicationDataRuntime?
                    .DecomposeGearCommands),
        transportFactory: secureTransportFactory);

Console.WriteLine($"Godswar .NET {Environment.Version.Major} server starting");
Console.WriteLine(
    "[startup] selected " +
    $"runtime={runtimeProfile.RuntimeProfile} " +
    $"storage={runtimeProfile.StorageProvider} " +
    $"transport={runtimeProfile.Transport}");
Console.WriteLine($"Runtime:      {runtimeProfile.RuntimeProfile}");
Console.WriteLine($"Storage:      {runtimeProfile.StorageProvider}");
Console.WriteLine(
    "[world-content] pinned " +
    $"source={worldContent.Manifest.Source} " +
    $"revision={worldContent.Manifest.Revision} " +
    $"maps={worldContent.Manifest.Maps.EntryCount} " +
    $"npcs={worldContent.Manifest.Npcs.EntryCount} " +
    $"monsters={worldContent.Manifest.Monsters.EntryCount} " +
    $"bootstrap={worldContent.Manifest.EnterBootstrap.EntryCount}");
if (legacyAuthenticationAccess is not null)
{
    Console.WriteLine(
        "[security] WARNING legacy authentication enabled " +
        "by explicit LocalDevelopment profile");
}
Console.WriteLine(
    rawCompatibilityEnabled
        ? $"Login server: {options.Login.BindHost}:{options.Login.Port}"
        : "Login server: raw compatibility disabled while secure mode is enabled");
Console.WriteLine(
    rawCompatibilityEnabled
        ? $"Game server:  {options.Game.BindHost}:{options.Game.Port} advertised as {options.Game.PublicHost}:{options.Game.Port}"
        : "Game server:  raw compatibility disabled while secure mode is enabled");
Console.WriteLine($"Monsters:     {options.Game.Monsters.Runtime} runtime");
Console.WriteLine($"Players:      {options.Game.Players.Runtime} runtime");
Console.WriteLine(
    $"Network:      active={options.Network.MaxActiveConnections}, " +
    $"unauthenticated={options.Network.MaxUnauthenticatedConnections}, " +
    $"reliable-egress={options.Network.ReliableEgressQueueItems} items/" +
    $"{options.Network.ReliableEgressQueueBytes} bytes");
Console.WriteLine(
    options.Secure.Enabled
        ? $"Secure TLS:   login={options.Secure.Login.BindHost}:{options.Secure.Login.Port}, " +
          $"game={options.Secure.Game.BindHost}:{options.Secure.Game.Port} (single-use ticket binding)"
        : "Secure TLS:   disabled");
Console.WriteLine(
    secureUdpRuntime is not null
        ? $"Secure UDP:   starting {options.Secure.Udp.BindHost}:{options.Secure.Udp.Port}"
        : "Secure UDP:   disabled; gameplay remains on TLS");
Console.WriteLine(
    postgresApplicationDataRuntime is null
        ? "PG outbox:    unavailable for JSON compatibility storage"
        : postgresApplicationDataRuntime.OutboxEnabled
            ? "PG outbox:    enabled"
            : "PG outbox:    dispatcher disabled; durable events retained");

var runtimeTasks = new List<Task>
{
    registry.RunMonsterRoamingAsync(shutdown.Token),
    registry.RunPlayerRecoveryAsync(shutdown.Token),
    registry.RunExperienceBoostStatusReconciliationAsync(shutdown.Token),
    registry.RunZodiacEnergyAccrualAsync(shutdown.Token)
};
var endpointServers = new List<TcpEndpointServer>(2);
var supervisedTasks = new List<Task>(4);

try
{
    if (postgresApplicationDataRuntime?.OutboxEnabled == true)
    {
        var outboxTask =
            postgresApplicationDataRuntime.RunOutboxAsync(
                shutdown.Token);
        runtimeTasks.Add(outboxTask);
        supervisedTasks.Add(outboxTask);
    }

    if (secureUdpRuntime is not null)
    {
        var udpTask = secureUdpRuntime.RunAsync(shutdown.Token);
        runtimeTasks.Add(udpTask);
        supervisedTasks.Add(udpTask);
        var udpEndpoint = await secureUdpRuntime.WaitUntilReadyAsync(
            shutdown.Token).WaitAsync(
                TimeSpan.FromSeconds(10),
                shutdown.Token);
        Console.WriteLine($"Secure UDP ready: {udpEndpoint}");
    }

    if (loginServer is not null && gameServer is not null)
    {
        endpointServers.Add(loginServer);
        endpointServers.Add(gameServer);
        var loginTask = loginServer.RunAsync(shutdown.Token);
        var gameTask = gameServer.RunAsync(shutdown.Token);
        runtimeTasks.Add(loginTask);
        runtimeTasks.Add(gameTask);
        supervisedTasks.Add(loginTask);
        supervisedTasks.Add(gameTask);
    }
    if (secureLoginServer is not null && secureGameServer is not null)
    {
        endpointServers.Add(secureLoginServer);
        endpointServers.Add(secureGameServer);
        var loginTask = secureLoginServer.RunAsync(shutdown.Token);
        var gameTask = secureGameServer.RunAsync(shutdown.Token);
        runtimeTasks.Add(loginTask);
        runtimeTasks.Add(gameTask);
        supervisedTasks.Add(loginTask);
        supervisedTasks.Add(gameTask);
    }

    if (endpointServers.Count != 2)
    {
        throw new InvalidOperationException(
            "Exactly one coherent login/game listener pair is required.");
    }

    foreach (var supervisedTask in supervisedTasks)
    {
        _ = supervisedTask.ContinueWith(
            static (_, state) =>
                ((CancellationTokenSource)state!).Cancel(),
            shutdown,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    await Task.WhenAll(endpointServers.Select(
        server => server.WaitUntilStartedAsync(shutdown.Token))).WaitAsync(
        TimeSpan.FromSeconds(10),
        shutdown.Token);
    ControlledHostPrivacyEvidence.RecordIfActive(
        ControlledHostEvidenceEvent.SecureListenersReady);
    Console.WriteLine(
        $"Listener profile ready: {listenerProfile.Transport} " +
        $"({listenerProfile.Login.Port}/{listenerProfile.Game.Port})");

    if (controlledHostShutdown is not null)
    {
        var controlTask =
            controlledHostShutdown.RunAsync(shutdown.Token);
        runtimeTasks.Add(controlTask);
        _ = controlTask.ContinueWith(
            static (_, state) =>
                ((CancellationTokenSource)state!).Cancel(),
            shutdown,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    await Task.WhenAll(runtimeTasks);
}
catch
{
    shutdown.Cancel();
    try
    {
        await Task.WhenAll(runtimeTasks);
    }
    catch
    {
        // Preserve the initiating startup/runtime exception below.
    }
    throw;
}
