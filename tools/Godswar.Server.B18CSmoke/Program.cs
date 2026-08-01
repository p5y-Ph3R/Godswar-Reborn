using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using Godswar.Server.Packets;

namespace Godswar.Server.B18CSmoke;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var stage = "configuration";
        Exception? failure = null;
        ManagedChildProcess? relay = null;
        ManagedChildProcess? worker = null;
        LegacySmokePeer? activeGame = null;
        SmokeWorkspace? workspace = null;
        var password = CreatePassword();
        using var deadline =
            new CancellationTokenSource(TimeSpan.FromSeconds(40));

        try
        {
            var options = SmokeArguments.Parse(args);
            workspace = SmokeWorkspace.Create();
            var endpoints = workspace.Endpoints;
            var loginName = CreateLoginName();
            var username = PacketText.DecodeLoginName(loginName);

            stage = "relay startup without worker";
            relay = StartRelay(options, workspace);
            var relayPid = relay.Id;
            await AssertRelayAliveWithoutWorkerAsync(
                relay,
                endpoints,
                deadline.Token);
            Console.WriteLine(
                "PASS relay remains alive while worker is unavailable");

            stage = "worker startup and readiness";
            worker = StartWorker(options, workspace);
            await WaitForWorkerReadyAsync(
                worker,
                endpoints.WorkerManagementPort,
                deadline.Token);
            Console.WriteLine("PASS worker /ready");

            stage = "first relayed raw protocol round";
            activeGame = await TwoProcessSmokeProtocol.OpenRoundAsync(
                endpoints,
                loginName,
                username,
                password,
                deadline.Token);
            Console.WriteLine(
                "PASS relayed login/select/public redirect and game bootstrap");

            stage = "worker stop with stable relay";
            await worker.DisposeAsync();
            worker = null;
            await TwoProcessSmokeProtocol
                .RequireActiveConnectionClosedAsync(
                    activeGame,
                    deadline.Token);
            await activeGame.DisposeAsync();
            activeGame = null;
            RequireStableRelay(relay, relayPid);
            await AssertRelayAliveWithoutWorkerAsync(
                relay,
                endpoints,
                deadline.Token);
            Console.WriteLine(
                "PASS worker stop closed active relay connection and relay PID remained stable");

            stage = "worker restart and readiness";
            worker = StartWorker(options, workspace);
            await WaitForWorkerReadyAsync(
                worker,
                endpoints.WorkerManagementPort,
                deadline.Token);
            Console.WriteLine("PASS restarted worker /ready");

            stage = "second relayed raw protocol round";
            await using (var restartedGame =
                await TwoProcessSmokeProtocol.OpenRoundAsync(
                    endpoints,
                    loginName,
                    username,
                    password,
                    deadline.Token))
            {
            }
            RequireStableRelay(relay, relayPid);
            Console.WriteLine(
                "PASS relayed protocol after worker restart");
            Console.WriteLine(
                $"PASS relay PID stable across worker restart pid={relayPid}");
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested)
        {
            failure = new TimeoutException(
                "The 40-second two-process smoke deadline expired.");
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (activeGame is not null)
            {
                try
                {
                    await activeGame.DisposeAsync();
                }
                catch (Exception error)
                {
                    cleanupFailure = error;
                }
            }

            var childCleanupFailure = await CleanupChildrenAsync(
                worker,
                relay);
            cleanupFailure ??= childCleanupFailure;
            if (workspace is not null)
            {
                try
                {
                    workspace.DeleteValidated();
                }
                catch (Exception error)
                {
                    cleanupFailure ??= error;
                }
            }

            CryptographicOperations.ZeroMemory(password);
            if (failure is null && cleanupFailure is not null)
            {
                stage = "cleanup";
                failure = cleanupFailure;
            }
        }

        if (failure is null)
        {
            Console.WriteLine("PASS B18C1 bounded two-process smoke");
            return 0;
        }

        Console.Error.WriteLine(
            $"FAIL {stage}: {Describe(failure)}");
        if (worker is not null)
        {
            Console.Error.WriteLine(worker.RenderLogTail());
        }
        if (relay is not null)
        {
            Console.Error.WriteLine(relay.RenderLogTail());
        }
        return 1;
    }

    private static ManagedChildProcess StartRelay(
        SmokeArguments options,
        SmokeWorkspace workspace) =>
        ManagedChildProcess.Start(
            "relay",
            options.DotnetHostPath,
            [
                options.ServerDllPath,
                "--relay-gateway",
                workspace.RelayOptionsPath
            ],
            workspace.DirectoryPath);

    private static ManagedChildProcess StartWorker(
        SmokeArguments options,
        SmokeWorkspace workspace) =>
        ManagedChildProcess.Start(
            "worker",
            options.DotnetHostPath,
            [
                options.ServerDllPath,
                workspace.WorkerOptionsPath
            ],
            workspace.DirectoryPath,
            workspace.CreateWorkerEnvironment(
                options.PostgresConnectionString));

    private static async Task AssertRelayAliveWithoutWorkerAsync(
        ManagedChildProcess relay,
        SmokeEndpoints endpoints,
        CancellationToken cancellationToken)
    {
        await AssertUnavailableEndpointAsync(
            relay,
            endpoints.RelayLoginPort,
            "login",
            cancellationToken);
        await AssertUnavailableEndpointAsync(
            relay,
            endpoints.RelayGamePort,
            "game",
            cancellationToken);
        relay.RequireRunning("worker-unavailable probe");
    }

    private static async Task AssertUnavailableEndpointAsync(
        ManagedChildProcess relay,
        int port,
        string role,
        CancellationToken cancellationToken)
    {
        using var startupTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        TcpClient? client = null;
        while (client is null)
        {
            relay.RequireRunning($"{role} listener startup");
            var candidate = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await candidate.ConnectAsync(
                    IPAddress.Loopback,
                    port,
                    startupTimeout.Token);
                client = candidate;
            }
            catch (SocketException)
            {
                candidate.Dispose();
                await Task.Delay(
                    TimeSpan.FromMilliseconds(50),
                    startupTimeout.Token);
            }
        }

        using (client)
        using (var closeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
        {
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var received = await client.GetStream().ReadAsync(
                    new byte[1],
                    closeTimeout.Token);
                if (received != 0)
                {
                    throw new InvalidDataException(
                        $"The {role} relay emitted bytes without a worker.");
                }
            }
            catch (IOException)
            {
                // A TCP reset is also a bounded unavailable response.
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The {role} relay did not close an unavailable-worker connection.");
            }
        }
    }

    private static async Task WaitForWorkerReadyAsync(
        ManagedChildProcess worker,
        int managementPort,
        CancellationToken cancellationToken)
    {
        using var readyTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var endpoint =
            new Uri($"http://127.0.0.1:{managementPort}/ready");

        while (true)
        {
            worker.RequireRunning("readiness probe");
            try
            {
                using var response = await client.GetAsync(
                    endpoint,
                    HttpCompletionOption.ResponseHeadersRead,
                    readyTimeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The worker did not become ready within 10 seconds.");
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                readyTimeout.Token);
        }
    }

    private static void RequireStableRelay(
        ManagedChildProcess relay,
        int expectedPid)
    {
        relay.RequireRunning("worker restart");
        if (relay.Id != expectedPid)
        {
            throw new InvalidOperationException(
                "The relay PID changed while the worker restarted.");
        }

        using var observed = Process.GetProcessById(expectedPid);
        if (observed.HasExited)
        {
            throw new InvalidOperationException(
                "The original relay PID is no longer running.");
        }
    }

    private static async Task<Exception?> CleanupChildrenAsync(
        ManagedChildProcess? worker,
        ManagedChildProcess? relay)
    {
        var disposals = new List<Task>(2);
        if (worker is not null)
        {
            disposals.Add(worker.DisposeAsync().AsTask());
        }
        if (relay is not null)
        {
            disposals.Add(relay.DisposeAsync().AsTask());
        }

        try
        {
            await Task.WhenAll(disposals);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static byte[] CreatePassword()
    {
        const string alphabet =
            "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = RandomNumberGenerator.GetBytes(24);
        try
        {
            var password = new byte[random.Length];
            for (var index = 0; index < random.Length; index++)
            {
                password[index] = (byte)alphabet[
                    random[index] % alphabet.Length];
            }

            return password;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    private static string CreateLoginName() =>
        $"b18c{Guid.NewGuid():N}"[..20];

    private static string Describe(Exception error)
    {
        var message = error.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        if (message.Length > 180)
        {
            message = message[..180];
        }

        return string.IsNullOrWhiteSpace(message)
            ? error.GetType().Name
            : $"{error.GetType().Name}: {message}";
    }

    private sealed record SmokeArguments(
        string ServerDllPath,
        string DotnetHostPath,
        string PostgresConnectionString)
    {
        private const string PostgresConnectionVariable =
            "GODSWAR_B18C_POSTGRES_CONNECTION_STRING";

        public static SmokeArguments Parse(string[] args)
        {
            string? serverDll = null;
            string? dotnetHost = null;
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(
                        "Smoke arguments must be name/value pairs.");
                }

                switch (args[index])
                {
                    case "--server-dll":
                        serverDll = args[index + 1];
                        break;
                    case "--dotnet-host":
                        dotnetHost = args[index + 1];
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown smoke argument '{args[index]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(serverDll))
            {
                throw new ArgumentException(
                    "--server-dll is required.");
            }

            serverDll = Path.GetFullPath(serverDll);
            if (string.IsNullOrWhiteSpace(dotnetHost))
            {
                throw new ArgumentException(
                    "--dotnet-host is required.");
            }

            dotnetHost = Path.GetFullPath(dotnetHost);
            if (!File.Exists(serverDll) ||
                !string.Equals(
                    Path.GetFileName(serverDll),
                    "Godswar.Server.dll",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException(
                    "The built src/Godswar.Server DLL was not found.");
            }
            if (!File.Exists(dotnetHost))
            {
                throw new FileNotFoundException(
                    "The dotnet host executable was not found.");
            }

            var postgresConnectionString =
                Environment.GetEnvironmentVariable(
                    PostgresConnectionVariable);
            if (string.IsNullOrWhiteSpace(postgresConnectionString))
            {
                throw new ArgumentException(
                    $"{PostgresConnectionVariable} is required and must " +
                    "target an isolated PostgreSQL smoke database.");
            }

            return new SmokeArguments(
                serverDll,
                dotnetHost,
                postgresConnectionString);
        }
    }
}
