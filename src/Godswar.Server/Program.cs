using Godswar.Server;
using Godswar.Server.Game;
using Godswar.Server.Networking;
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
var loginServer = new TcpEndpointServer(
    NetworkEndpointRole.Login,
    options.Login.BindHost,
    options.Login.Port,
    options.Network,
    admission,
    session => new LoginClientHandler(session, store, options));

var gameServer = new TcpEndpointServer(
    NetworkEndpointRole.Game,
    options.Game.BindHost,
    options.Game.Port,
    options.Network,
    admission,
    session => new GameClientHandler(session, store, registry, options.Game.DeveloperCommands));

Console.WriteLine($"Godswar .NET {Environment.Version.Major} server starting");
Console.WriteLine($"Storage:      {options.Storage.Provider}");
Console.WriteLine($"Login server: {options.Login.BindHost}:{options.Login.Port}");
Console.WriteLine($"Game server:  {options.Game.BindHost}:{options.Game.Port} advertised as {options.Game.PublicHost}:{options.Game.Port}");
Console.WriteLine($"Monsters:     {options.Game.Monsters.Runtime} runtime");
Console.WriteLine($"Players:      {options.Game.Players.Runtime} runtime");
Console.WriteLine(
    $"Network:      active={options.Network.MaxActiveConnections}, " +
    $"unauthenticated={options.Network.MaxUnauthenticatedConnections}, " +
    $"reliable-egress={options.Network.ReliableEgressQueueItems} items/" +
    $"{options.Network.ReliableEgressQueueBytes} bytes");

await Task.WhenAll(
    loginServer.RunAsync(shutdown.Token),
    gameServer.RunAsync(shutdown.Token),
    registry.RunMonsterRoamingAsync(shutdown.Token),
    registry.RunPlayerRecoveryAsync(shutdown.Token),
    registry.RunExperienceBoostStatusReconciliationAsync(shutdown.Token),
    registry.RunZodiacEnergyAccrualAsync(shutdown.Token));
