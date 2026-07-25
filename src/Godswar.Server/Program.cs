using Godswar.Server;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

var optionsPath = args.Length > 0 ? args[0] : "appsettings.json";
var options = ServerOptions.Load(optionsPath);
await using IGameStore store = options.Storage.Provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
    ? new PostgresGameStore(options.Storage.PostgresConnectionString)
    : new JsonGameStore(options.DataPath);
await store.EnsureSeedDataAsync();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

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
        session => new LoginClientHandler(session, store, options))
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
            options.Game.DeveloperCommands))
    : null;

using SecureServerCertificate? secureCertificate =
    options.Secure.Enabled
        ? SecureServerCertificate.Load(options.Secure)
        : null;
var secureGameTarget = options.Secure.Enabled
    ? options.Secure.BuildGameTarget()
    : null;
using InMemoryGameTicketStore? secureGameTickets =
    options.Secure.Enabled
        ? new InMemoryGameTicketStore(
            options.Secure.Tickets.Capacity,
            options.Secure.Tickets.Ttl)
        : null;
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
            gameTarget: secureGameTarget)
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
            options.Game.DeveloperCommands),
        transportFactory: secureTransportFactory);

Console.WriteLine($"Godswar .NET {Environment.Version.Major} server starting");
Console.WriteLine($"Storage:      {options.Storage.Provider}");
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

var runtimeTasks = new List<Task>
{
    registry.RunMonsterRoamingAsync(shutdown.Token),
    registry.RunPlayerRecoveryAsync(shutdown.Token),
    registry.RunExperienceBoostStatusReconciliationAsync(shutdown.Token),
    registry.RunZodiacEnergyAccrualAsync(shutdown.Token)
};
var endpointServers = new List<TcpEndpointServer>(2);
if (loginServer is not null && gameServer is not null)
{
    endpointServers.Add(loginServer);
    endpointServers.Add(gameServer);
    runtimeTasks.Add(loginServer.RunAsync(shutdown.Token));
    runtimeTasks.Add(gameServer.RunAsync(shutdown.Token));
}
if (secureLoginServer is not null && secureGameServer is not null)
{
    endpointServers.Add(secureLoginServer);
    endpointServers.Add(secureGameServer);
    runtimeTasks.Add(secureLoginServer.RunAsync(shutdown.Token));
    runtimeTasks.Add(secureGameServer.RunAsync(shutdown.Token));
}

if (endpointServers.Count != 2)
{
    throw new InvalidOperationException(
        "Exactly one coherent login/game listener pair is required.");
}

var endpointTasks = runtimeTasks.Skip(runtimeTasks.Count - 2).ToArray();
foreach (var endpointTask in endpointTasks)
{
    _ = endpointTask.ContinueWith(
        static (_, state) =>
            ((CancellationTokenSource)state!).Cancel(),
        shutdown,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

try
{
    await Task.WhenAll(endpointServers.Select(
        server => server.WaitUntilStartedAsync(shutdown.Token))).WaitAsync(
        TimeSpan.FromSeconds(10),
        shutdown.Token);
    Console.WriteLine(
        $"Listener profile ready: {listenerProfile.Transport} " +
        $"({listenerProfile.Login.Port}/{listenerProfile.Game.Port})");
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
