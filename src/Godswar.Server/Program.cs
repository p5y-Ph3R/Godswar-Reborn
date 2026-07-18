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

var registry = new GameSessionRegistry(store);
var loginServer = new TcpEndpointServer(
    "login",
    options.Login.BindHost,
    options.Login.Port,
    session => new LoginClientHandler(session, store, options));

var gameServer = new TcpEndpointServer(
    "game",
    options.Game.BindHost,
    options.Game.Port,
    session => new GameClientHandler(session, store, registry));

Console.WriteLine($"Godswar .NET {Environment.Version.Major} server starting");
Console.WriteLine($"Storage:      {options.Storage.Provider}");
Console.WriteLine($"Login server: {options.Login.BindHost}:{options.Login.Port}");
Console.WriteLine($"Game server:  {options.Game.BindHost}:{options.Game.Port} advertised as {options.Game.PublicHost}:{options.Game.Port}");

await Task.WhenAll(
    loginServer.RunAsync(shutdown.Token),
    gameServer.RunAsync(shutdown.Token),
    registry.RunMonsterRoamingAsync(shutdown.Token),
    registry.RunPlayerRecoveryAsync(shutdown.Token));
