using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Godswar.Server.B18CSmoke;

internal readonly record struct SmokeEndpoints(
    int RelayLoginPort,
    int RelayGamePort,
    int WorkerLoginPort,
    int WorkerGamePort,
    int WorkerManagementPort);

internal sealed class SmokeWorkspace
{
    private const string DirectoryPrefix = "godswar-b18c-smoke-";
    private const string MarkerName = ".b18c-smoke-owner";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string _markerToken;
    private readonly string _temporaryRoot;

    private SmokeWorkspace(
        string directoryPath,
        string temporaryRoot,
        string markerToken,
        SmokeEndpoints endpoints)
    {
        DirectoryPath = directoryPath;
        _temporaryRoot = temporaryRoot;
        _markerToken = markerToken;
        Endpoints = endpoints;
        RelayOptionsPath = Path.Combine(
            directoryPath,
            "relay-gateway.json");
        WorkerOptionsPath = Path.Combine(
            directoryPath,
            "worker.json");
    }

    public string DirectoryPath { get; }

    public SmokeEndpoints Endpoints { get; }

    public string RelayOptionsPath { get; }

    public string WorkerOptionsPath { get; }

    public static SmokeWorkspace Create()
    {
        var root = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var suffix = Guid.NewGuid().ToString("N");
        var path = Path.GetFullPath(
            Path.Combine(root, DirectoryPrefix + suffix));
        var token = Guid.NewGuid().ToString("N");
        var endpoints = AllocateEndpoints();
        var workspace = new SmokeWorkspace(
            path,
            root,
            token,
            endpoints);

        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(
                Path.Combine(path, MarkerName),
                token,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));
            workspace.WriteConfigurations();
            return workspace;
        }
        catch
        {
            try
            {
                workspace.DeleteValidated();
            }
            catch
            {
                // Refusing an unvalidated deletion is safer than broadening
                // cleanup after partial workspace creation.
            }

            throw;
        }
    }

    public IReadOnlyDictionary<string, string>
        CreateWorkerEnvironment(string postgresConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            postgresConnectionString);
        var invariant = CultureInfo.InvariantCulture;
        return new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["GODSWAR_RUNTIME_PROFILE"] = "LocalDevelopment",
            ["GODSWAR_STORAGE_PROVIDER"] = "Postgres",
            ["GODSWAR_POSTGRES_CONNECTION_STRING"] =
                postgresConnectionString,
            ["GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION"] =
                "true",
            ["GODSWAR_LOGIN_BIND_HOST"] = "127.0.0.1",
            ["GODSWAR_LOGIN_PORT"] =
                Endpoints.WorkerLoginPort.ToString(invariant),
            ["GODSWAR_GAME_BIND_HOST"] = "127.0.0.1",
            ["GODSWAR_GAME_PORT"] =
                Endpoints.WorkerGamePort.ToString(invariant),
            ["GODSWAR_GAME_PUBLIC_HOST"] = "127.0.0.1",
            ["GODSWAR_GAME_PUBLIC_PORT"] =
                Endpoints.RelayGamePort.ToString(invariant),
            ["GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID"] =
                "b18c-smoke-worker",
            ["GODSWAR_MONSTER_RUNTIME"] = "Ecs",
            ["GODSWAR_PLAYER_RUNTIME"] = "Ecs",
            ["GODSWAR_ZODIAC_ENERGY_ENABLED"] = "false",
            ["GODSWAR_MANAGEMENT_ENABLED"] = "true",
            ["GODSWAR_MANAGEMENT_BIND_HOST"] = "127.0.0.1",
            ["GODSWAR_MANAGEMENT_PORT"] =
                Endpoints.WorkerManagementPort.ToString(invariant)
        };
    }

    public void DeleteValidated()
    {
        var fullPath = Path.GetFullPath(DirectoryPath);
        var parent = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        var suffix = name.StartsWith(
                DirectoryPrefix,
                StringComparison.Ordinal)
            ? name[DirectoryPrefix.Length..]
            : string.Empty;
        var markerPath = Path.Combine(fullPath, MarkerName);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(parent, _temporaryRoot, comparison) ||
            !Guid.TryParseExact(suffix, "N", out _) ||
            !Directory.Exists(fullPath) ||
            File.GetAttributes(fullPath).HasFlag(
                FileAttributes.ReparsePoint) ||
            !File.Exists(markerPath) ||
            !string.Equals(
                File.ReadAllText(markerPath),
                _markerToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refused to delete an unvalidated smoke directory.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private static SmokeEndpoints AllocateEndpoints()
    {
        var listeners = new List<TcpListener>(5);
        var ports = new List<int>(5);
        try
        {
            for (var index = 0; index < 5; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                ports.Add(
                    ((IPEndPoint)listener.LocalEndpoint).Port);
            }
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }

        return new SmokeEndpoints(
            ports[0],
            ports[1],
            ports[2],
            ports[3],
            ports[4]);
    }

    private void WriteConfigurations()
    {
        var relay = new
        {
            login = new
            {
                bindHost = "127.0.0.1",
                bindPort = Endpoints.RelayLoginPort,
                upstreamHost = "127.0.0.1",
                upstreamPort = Endpoints.WorkerLoginPort
            },
            game = new
            {
                bindHost = "127.0.0.1",
                bindPort = Endpoints.RelayGamePort,
                upstreamHost = "127.0.0.1",
                upstreamPort = Endpoints.WorkerGamePort
            },
            limits = new
            {
                listenBacklog = 16,
                maximumConnections = 16,
                bufferSizeBytes = 4 * 1024,
                connectTimeoutMilliseconds = 250,
                idleTimeoutMilliseconds = 5_000,
                writeTimeoutMilliseconds = 1_000,
                drainTimeoutMilliseconds = 1_000
            }
        };
        var worker = new
        {
            runtimeProfile = "LocalDevelopment",
            login = new
            {
                bindHost = "127.0.0.1",
                port = Endpoints.WorkerLoginPort
            },
            game = new
            {
                bindHost = "127.0.0.1",
                port = Endpoints.WorkerGamePort,
                publicHost = "127.0.0.1",
                publicPort = Endpoints.RelayGamePort,
                monsters = new { runtime = "Ecs" },
                players = new { runtime = "Ecs" },
                worldInstances = new
                {
                    serverNodeId = "b18c-smoke-worker"
                },
                zodiacEnergy = new { enabled = false }
            },
            secure = new { enabled = false },
            authentication = new
            {
                allowLegacyRawAuthentication = true
            },
            storage = new
            {
                provider = "Postgres",
                postgresConnectionString = string.Empty
            },
            operations = new
            {
                management = new
                {
                    enabled = true,
                    bindHost = "127.0.0.1",
                    port = Endpoints.WorkerManagementPort
                },
                readiness = new
                {
                    pollIntervalMilliseconds = 100
                }
            }
        };

        File.WriteAllText(
            RelayOptionsPath,
            JsonSerializer.Serialize(relay, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            WorkerOptionsPath,
            JsonSerializer.Serialize(worker, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
