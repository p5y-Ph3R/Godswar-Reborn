using System.Net;
using Godswar.Server.Networking.RelayGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RelayGatewayChecks
{
    public const string CheckName =
        "B18C1 bounded opaque relay gateway";

    public static async Task RunAsync()
    {
        await CheckConfigurationAsync();
        CheckArchitectureBoundary();
        await CheckOpaqueRoundTripsAsync();
        await CheckWorkerRecoveryAsync();
        await CheckIdleDeadlineAsync();
        await CheckAdmissionAndDrainAsync();
    }

    private static async Task CheckConfigurationAsync()
    {
        var valid = CreateOptions();
        var snapshot = await valid.ValidateAsync();
        Check.Equal(
            512,
            snapshot.Limits.MaximumConnections,
            "validated relay connection capacity");
        Check.True(
            snapshot.Login.Upstream.Address.Equals(IPAddress.Loopback),
            "relay resolves the private login upstream once");
        var checkedIn = await RelayGatewayOptions.LoadAsync(Path.Combine(
            FindRepositoryRoot(),
            "appsettings.relay-gateway.json"));
        Check.Equal(
            7_000,
            checkedIn.Game.Bind.Port,
            "checked-in relay preserves the original public game port");
        Check.Equal(
            17_000,
            checkedIn.Game.Upstream.Port,
            "checked-in relay targets the private worker game port");

        var publicUpstream = CreateOptions();
        publicUpstream.Login!.UpstreamHost = "8.8.8.8";
        await ExpectThrowsAsync<InvalidDataException>(
            () => publicUpstream.ValidateAsync(),
            "public relay upstream is rejected");

        var listenerCollision = CreateOptions();
        listenerCollision.Game!.BindPort =
            listenerCollision.Login!.BindPort;
        await ExpectThrowsAsync<InvalidDataException>(
            () => listenerCollision.ValidateAsync(),
            "login and game listener collision is rejected");

        var relayLoop = CreateOptions();
        relayLoop.Login!.UpstreamPort = relayLoop.Login.BindPort;
        await ExpectThrowsAsync<InvalidDataException>(
            () => relayLoop.ValidateAsync(),
            "relay listener loop is rejected");

        var invalidCapacity = CreateOptions();
        invalidCapacity.Limits.MaximumConnections = 0;
        await ExpectThrowsAsync<InvalidDataException>(
            () => invalidCapacity.ValidateAsync(),
            "zero relay capacity is rejected");

        var directory = Path.Combine(
            Path.GetTempPath(),
            "godswar-relay-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "relay.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "login": {
                    "bindHost": "127.0.0.1",
                    "bindPort": 31001,
                    "upstreamHost": "127.0.0.1",
                    "upstreamPort": 31002
                  },
                  "game": {
                    "bindHost": "127.0.0.1",
                    "bindPort": 31003,
                    "upstreamHost": "127.0.0.1",
                    "upstreamPort": 31004
                  },
                  "limits": {
                    "unknownLimit": 1
                  }
                }
                """);
            await ExpectThrowsAsync<InvalidDataException>(
                () => RelayGatewayOptions.LoadAsync(path),
                "unknown relay configuration members fail closed");

            var oversizedPath = Path.Combine(directory, "oversized.json");
            await File.WriteAllBytesAsync(
                oversizedPath,
                new byte[
                    RelayGatewayOptions.MaximumConfigurationBytes + 1]);
            await ExpectThrowsAsync<InvalidDataException>(
                () => RelayGatewayOptions.LoadAsync(oversizedPath),
                "oversized relay configuration is bounded before parsing");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckArchitectureBoundary()
    {
        var root = FindRepositoryRoot();
        var relayRoot = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Networking",
            "RelayGateway");
        var sources = Directory
            .EnumerateFiles(relayRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            sources.Length >= 6,
            "relay gateway remains a dedicated networking boundary");

        var forbidden = new[]
        {
            "Godswar.Server.Game",
            "Godswar.Server.State",
            "Npgsql",
            "PacketCipher",
            "Opcodes",
            "GamePacket"
        };
        foreach (var sourcePath in sources)
        {
            var source = File.ReadAllText(sourcePath);
            foreach (var token in forbidden)
            {
                Check.True(
                    !source.Contains(token, StringComparison.Ordinal),
                    $"{Path.GetFileName(sourcePath)} does not depend on {token}");
            }
        }

        var program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Program.cs"));
        var relayIndex = program.IndexOf(
            "RelayGatewayCommand.TryRunAsync(args)",
            StringComparison.Ordinal);
        var optionsIndex = program.IndexOf(
            "ServerRuntimeBootstrap.TryLoadOptions(",
            StringComparison.Ordinal);
        Check.True(
            relayIndex >= 0 && relayIndex < optionsIndex,
            "relay mode runs before game, database, and ECS composition");

        var checkedInOptions = Path.Combine(
            root,
            "appsettings.relay-gateway.json");
        Check.True(
            File.Exists(checkedInOptions),
            "bounded local relay example is checked in");
    }

    private static RelayGatewayOptions CreateOptions() =>
        new()
        {
            Login = new RelayGatewayEndpointOptions
            {
                BindHost = "127.0.0.1",
                BindPort = 31_001,
                UpstreamHost = "127.0.0.1",
                UpstreamPort = 31_002
            },
            Game = new RelayGatewayEndpointOptions
            {
                BindHost = "127.0.0.1",
                BindPort = 31_003,
                UpstreamHost = "127.0.0.1",
                UpstreamPort = 31_004
            }
        };

    private static RelayGatewayConfiguration CreateConfiguration(
        IPEndPoint loginUpstream,
        IPEndPoint gameUpstream,
        int maximumConnections = 8,
        int drainMilliseconds = 500) =>
        new(
            new RelayGatewayEndpointConfiguration(
                RelayGatewayEndpointRole.Login,
                new IPEndPoint(IPAddress.Loopback, 0),
                loginUpstream),
            new RelayGatewayEndpointConfiguration(
                RelayGatewayEndpointRole.Game,
                new IPEndPoint(IPAddress.Loopback, 0),
                gameUpstream),
            new RelayGatewayRuntimeLimits(
                ListenBacklog: 16,
                MaximumConnections: maximumConnections,
                BufferSizeBytes: 1_024,
                ConnectTimeout: TimeSpan.FromMilliseconds(250),
                IdleTimeout: TimeSpan.FromSeconds(2),
                WriteTimeout: TimeSpan.FromMilliseconds(500),
                DrainTimeout: TimeSpan.FromMilliseconds(drainMilliseconds)));

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "GodswarServer.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root.");
    }
}
