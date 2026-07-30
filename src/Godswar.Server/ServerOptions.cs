using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Operations;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server;

internal sealed class ServerOptions
{
    public string RuntimeProfile { get; set; } = string.Empty;

    public EndpointOptions Login { get; set; } = new()
    {
        BindHost = "0.0.0.0",
        Port = 5999
    };

    public GameEndpointOptions Game { get; set; } = new()
    {
        BindHost = "0.0.0.0",
        PublicHost = "127.1.1.110",
        Port = 7000
    };

    public string DataPath { get; set; } = "data";

    public StorageOptions Storage { get; set; } = new();

    public NetworkRuntimeOptions Network { get; set; } = new();

    public SecureNetworkOptions Secure { get; set; } = new();

    public AuthenticationOptions Authentication { get; set; } = new();

    public ServerOperationsOptions Operations { get; set; } = new();

    public static ServerOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServerStartupConfigurationException(
                ServerStartupRejectionReason.OptionsFileMissing,
                "The server options file does not exist.");
        }

        var options = JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), JsonDefaults.Indented)
            ?? new ServerOptions();

        options.ApplyEnvironment().Normalize(path);
        ServerRuntimeProfilePolicy.Validate(options);
        return options;
    }

    private ServerOptions ApplyEnvironment()
    {
        Login ??= new EndpointOptions();
        Game ??= new GameEndpointOptions();
        Storage ??= new StorageOptions();
        Storage.Outbox ??= new PostgresOutboxDispatcherOptions();
        Storage.Checkpoints ??= new CharacterCheckpointWorkerOptions();
        Game.DeveloperCommands ??= new DeveloperCommandOptions();
        Game.ZodiacEnergy ??= new ZodiacEnergyOptions();
        Game.Monsters ??= new MonsterRuntimeOptions();
        Game.Players ??= new PlayerRuntimeOptions();
        Network ??= new NetworkRuntimeOptions();
        Secure ??= new SecureNetworkOptions();
        Authentication ??= new AuthenticationOptions();
        Operations ??= new ServerOperationsOptions();
        Secure.ApplyEnvironment();
        Operations.ApplyEnvironment();
        Authentication.Iterations = ReadInt(
            "GODSWAR_AUTH_ITERATIONS",
            Authentication.Iterations);
        Authentication.MinimumStoredIterations = ReadInt(
            "GODSWAR_AUTH_MINIMUM_STORED_ITERATIONS",
            Authentication.MinimumStoredIterations);
        Authentication.MaximumStoredIterations = ReadInt(
            "GODSWAR_AUTH_MAXIMUM_STORED_ITERATIONS",
            Authentication.MaximumStoredIterations);
        Authentication.MaximumConcurrentKdfs = ReadInt(
            "GODSWAR_AUTH_MAXIMUM_CONCURRENT_KDFS",
            Authentication.MaximumConcurrentKdfs);
        Authentication.QueueCapacity = ReadInt(
            "GODSWAR_AUTH_QUEUE_CAPACITY",
            Authentication.QueueCapacity);
        Authentication.QueueCredentialBytes = ReadInt(
            "GODSWAR_AUTH_QUEUE_CREDENTIAL_BYTES",
            Authentication.QueueCredentialBytes);
        Authentication.QueueAdmissionTimeoutMilliseconds = ReadInt(
            "GODSWAR_AUTH_QUEUE_ADMISSION_TIMEOUT_MILLISECONDS",
            Authentication.QueueAdmissionTimeoutMilliseconds);
        Authentication.OperationTimeoutMilliseconds = ReadInt(
            "GODSWAR_AUTH_OPERATION_TIMEOUT_MILLISECONDS",
            Authentication.OperationTimeoutMilliseconds);
        Authentication.AllowRegistration = ReadBool(
            "GODSWAR_AUTH_ALLOW_REGISTRATION",
            Authentication.AllowRegistration);
        Authentication.AllowPlaintextMigration = ReadBool(
            "GODSWAR_AUTH_ALLOW_PLAINTEXT_MIGRATION",
            Authentication.AllowPlaintextMigration);
        Login.BindHost = Environment.GetEnvironmentVariable("GODSWAR_LOGIN_BIND_HOST") ?? Login.BindHost;
        Login.Port = ReadInt("GODSWAR_LOGIN_PORT", Login.Port);
        Game.BindHost = Environment.GetEnvironmentVariable("GODSWAR_GAME_BIND_HOST") ?? Game.BindHost;
        Game.Port = ReadInt("GODSWAR_GAME_PORT", Game.Port);
        Game.PublicHost = Environment.GetEnvironmentVariable("GODSWAR_GAME_PUBLIC_HOST") ?? Game.PublicHost;
        Game.Monsters.Runtime = ReadMonsterRuntime(
            "GODSWAR_MONSTER_RUNTIME",
            Game.Monsters.Runtime);
        Game.Players.Runtime = ReadPlayerRuntime(
            "GODSWAR_PLAYER_RUNTIME",
            Game.Players.Runtime);
        Game.DeveloperCommands.Enabled = ReadBool(
            "GODSWAR_DEVELOPER_COMMANDS_ENABLED",
            Game.DeveloperCommands.Enabled);
        Game.ZodiacEnergy.Enabled = ReadBool(
            "GODSWAR_ZODIAC_ENERGY_ENABLED",
            Game.ZodiacEnergy.Enabled);
        Game.ZodiacEnergy.TickSeconds = ReadInt(
            "GODSWAR_ZODIAC_ENERGY_TICK_SECONDS",
            Game.ZodiacEnergy.TickSeconds);
        Game.ZodiacEnergy.BoostedDailySeconds = ReadInt(
            "GODSWAR_ZODIAC_BOOSTED_DAILY_SECONDS",
            Game.ZodiacEnergy.BoostedDailySeconds);
        Game.ZodiacEnergy.EmulatorBoostedEnergyPerTickX100 = ReadInt(
            "GODSWAR_ZODIAC_EMULATOR_BOOSTED_ENERGY_PER_TICK_X100",
            Game.ZodiacEnergy.EmulatorBoostedEnergyPerTickX100);
        Game.ZodiacEnergy.EmulatorNormalEnergyPerTickX100 = ReadInt(
            "GODSWAR_ZODIAC_EMULATOR_NORMAL_ENERGY_PER_TICK_X100",
            Game.ZodiacEnergy.EmulatorNormalEnergyPerTickX100);
        Game.ZodiacEnergy.CompensationOnlineThresholdSeconds = ReadInt(
            "GODSWAR_ZODIAC_COMPENSATION_ONLINE_THRESHOLD_SECONDS",
            Game.ZodiacEnergy.CompensationOnlineThresholdSeconds);
        Game.ZodiacEnergy.CompensationSeconds = ReadInt(
            "GODSWAR_ZODIAC_COMPENSATION_SECONDS",
            Game.ZodiacEnergy.CompensationSeconds);
        Game.ZodiacEnergy.ServerUtcOffsetMinutes = ReadInt(
            "GODSWAR_ZODIAC_SERVER_UTC_OFFSET_MINUTES",
            Game.ZodiacEnergy.ServerUtcOffsetMinutes);
        Game.ZodiacEnergy.PersistenceIntervalSeconds = ReadInt(
            "GODSWAR_ZODIAC_PERSISTENCE_INTERVAL_SECONDS",
            Game.ZodiacEnergy.PersistenceIntervalSeconds);
        var developerAccountIds = Environment.GetEnvironmentVariable("GODSWAR_DEVELOPER_ACCOUNT_IDS");
        if (!string.IsNullOrWhiteSpace(developerAccountIds))
        {
            Game.DeveloperCommands.AllowedAccountIds = developerAccountIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var accountId) ? accountId : 0)
                .Where(accountId => accountId > 0)
                .Distinct()
                .ToArray();
        }

        DataPath = Environment.GetEnvironmentVariable("GODSWAR_DATA_PATH") ?? DataPath;
        RuntimeProfile =
            Environment.GetEnvironmentVariable(
                "GODSWAR_RUNTIME_PROFILE") ??
            RuntimeProfile;
        Storage.Provider = Environment.GetEnvironmentVariable("GODSWAR_STORAGE_PROVIDER") ?? Storage.Provider;
        Storage.PostgresConnectionString = Environment.GetEnvironmentVariable("GODSWAR_POSTGRES_CONNECTION_STRING")
            ?? Storage.PostgresConnectionString;
        Storage.Outbox.Enabled = ReadBool(
            "GODSWAR_OUTBOX_ENABLED",
            Storage.Outbox.Enabled);
        Storage.Outbox.BatchSize = ReadInt(
            "GODSWAR_OUTBOX_BATCH_SIZE",
            Storage.Outbox.BatchSize);
        Storage.Outbox.PollIntervalMilliseconds = ReadInt(
            "GODSWAR_OUTBOX_POLL_INTERVAL_MILLISECONDS",
            Storage.Outbox.PollIntervalMilliseconds);
        Storage.Outbox.LeaseMilliseconds = ReadInt(
            "GODSWAR_OUTBOX_LEASE_MILLISECONDS",
            Storage.Outbox.LeaseMilliseconds);
        Storage.Outbox.MaximumDeliveryAttempts = ReadInt(
            "GODSWAR_OUTBOX_MAXIMUM_DELIVERY_ATTEMPTS",
            Storage.Outbox.MaximumDeliveryAttempts);
        Storage.Outbox.BaseRetryDelayMilliseconds = ReadInt(
            "GODSWAR_OUTBOX_BASE_RETRY_DELAY_MILLISECONDS",
            Storage.Outbox.BaseRetryDelayMilliseconds);
        Storage.Outbox.MaximumRetryDelayMilliseconds = ReadInt(
            "GODSWAR_OUTBOX_MAXIMUM_RETRY_DELAY_MILLISECONDS",
            Storage.Outbox.MaximumRetryDelayMilliseconds);
        Storage.Outbox.GapRetryDelayMilliseconds = ReadInt(
            "GODSWAR_OUTBOX_GAP_RETRY_DELAY_MILLISECONDS",
            Storage.Outbox.GapRetryDelayMilliseconds);
        Storage.Outbox.CommandTimeoutMilliseconds = ReadInt(
            "GODSWAR_OUTBOX_COMMAND_TIMEOUT_MILLISECONDS",
            Storage.Outbox.CommandTimeoutMilliseconds);
        Storage.Checkpoints.QueueCapacity = ReadInt(
            "GODSWAR_CHECKPOINT_QUEUE_CAPACITY",
            Storage.Checkpoints.QueueCapacity);
        Storage.Checkpoints.WorkerCount = ReadInt(
            "GODSWAR_CHECKPOINT_WORKER_COUNT",
            Storage.Checkpoints.WorkerCount);
        Storage.Checkpoints.DirectOperationConcurrency = ReadInt(
            "GODSWAR_CHECKPOINT_DIRECT_OPERATION_CONCURRENCY",
            Storage.Checkpoints.DirectOperationConcurrency);
        Storage.Checkpoints.DirectAdmissionTimeoutMilliseconds = ReadInt(
            "GODSWAR_CHECKPOINT_DIRECT_ADMISSION_TIMEOUT_MILLISECONDS",
            Storage.Checkpoints.DirectAdmissionTimeoutMilliseconds);
        Storage.Checkpoints.CommandTimeoutMilliseconds = ReadInt(
            "GODSWAR_CHECKPOINT_COMMAND_TIMEOUT_MILLISECONDS",
            Storage.Checkpoints.CommandTimeoutMilliseconds);
        Storage.Checkpoints.BaseRetryDelayMilliseconds = ReadInt(
            "GODSWAR_CHECKPOINT_BASE_RETRY_DELAY_MILLISECONDS",
            Storage.Checkpoints.BaseRetryDelayMilliseconds);
        Storage.Checkpoints.MaximumRetryDelayMilliseconds = ReadInt(
            "GODSWAR_CHECKPOINT_MAXIMUM_RETRY_DELAY_MILLISECONDS",
            Storage.Checkpoints.MaximumRetryDelayMilliseconds);
        Storage.Checkpoints.MaximumRetryAgeMilliseconds = ReadInt(
            "GODSWAR_CHECKPOINT_MAXIMUM_RETRY_AGE_MILLISECONDS",
            Storage.Checkpoints.MaximumRetryAgeMilliseconds);
        Storage.Checkpoints.ShutdownDrainTimeoutMilliseconds = ReadInt(
            "GODSWAR_CHECKPOINT_SHUTDOWN_DRAIN_TIMEOUT_MILLISECONDS",
            Storage.Checkpoints.ShutdownDrainTimeoutMilliseconds);

        return this;
    }

    private ServerOptions Normalize(string optionsPath)
    {
        if (string.IsNullOrWhiteSpace(DataPath))
        {
            DataPath = "data";
        }

        if (!Path.IsPathRooted(DataPath))
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(optionsPath)) ?? Environment.CurrentDirectory;
            DataPath = Path.GetFullPath(Path.Combine(root, DataPath));
        }

        Game.DeveloperCommands ??= new DeveloperCommandOptions();
        Game.ZodiacEnergy ??= new ZodiacEnergyOptions();
        Game.Monsters ??= new MonsterRuntimeOptions();
        Game.Players ??= new PlayerRuntimeOptions();
        Network ??= new NetworkRuntimeOptions();
        Secure ??= new SecureNetworkOptions();
        Authentication ??= new AuthenticationOptions();
        Operations ??= new ServerOperationsOptions();
        Storage ??= new StorageOptions();
        Storage.Outbox ??= new PostgresOutboxDispatcherOptions();
        Storage.Checkpoints ??= new CharacterCheckpointWorkerOptions();
        Game.DeveloperCommands.AllowedAccountIds = (Game.DeveloperCommands.AllowedAccountIds ?? [])
            .Where(accountId => accountId > 0)
            .Distinct()
            .ToArray();
        Game.ZodiacEnergy.Normalize();
        Game.Monsters.Validate();
        Game.Players.Validate();
        Network.Validate();
        Authentication.Validate();
        Storage.Outbox.Validate();
        Storage.Checkpoints.Validate();
        Secure.NormalizeAndValidate(optionsPath, Login.Port, Game.Port);
        Operations.Validate(
            Login.Port,
            Game.Port,
            Secure.Login.Port,
            Secure.Game.Port);
        if (Secure.Enabled)
        {
            SecureNetworkOptions.ValidateSecureRuntime(Network);
        }

        return this;
    }

    private static int ReadInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null)
        {
            return fallback;
        }
        if (int.TryParse(raw, out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"{name} must be a valid integer.");
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null)
        {
            return fallback;
        }
        if (bool.TryParse(raw, out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"{name} must be 'true' or 'false'.");
    }

    private static MonsterRuntimeMode ReadMonsterRuntime(
        string name,
        MonsterRuntimeMode fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (Enum.TryParse<MonsterRuntimeMode>(
                raw,
                ignoreCase: true,
                out var mode) &&
            Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new InvalidDataException(
            $"{name} must be 'Legacy' or 'Ecs', but was '{raw}'.");
    }

    private static PlayerRuntimeMode ReadPlayerRuntime(
        string name,
        PlayerRuntimeMode fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (Enum.TryParse<PlayerRuntimeMode>(
                raw,
                ignoreCase: true,
                out var mode) &&
            Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new InvalidDataException(
            $"{name} must be 'Legacy' or 'Ecs', but was '{raw}'.");
    }
}

internal class EndpointOptions
{
    public string BindHost { get; set; } = "0.0.0.0";

    public int Port { get; set; }
}

internal sealed class GameEndpointOptions : EndpointOptions
{
    public string PublicHost { get; set; } = "127.1.1.110";

    public DeveloperCommandOptions DeveloperCommands { get; set; } = new();

    public ZodiacEnergyOptions ZodiacEnergy { get; set; } = new();

    public MonsterRuntimeOptions Monsters { get; set; } = new();

    public PlayerRuntimeOptions Players { get; set; } = new();
}

internal sealed class ZodiacEnergyOptions
{
    public bool Enabled { get; set; } = true;

    public int TickSeconds { get; set; } = 5 * 60;

    public int BoostedDailySeconds { get; set; } = 3 * 60 * 60;

    // The cadence is sourced, but retail captures have not established these
    // numeric awards. Keep both values explicit emulator policy.
    public int EmulatorBoostedEnergyPerTickX100 { get; set; } = 20 * 100;

    public int EmulatorNormalEnergyPerTickX100 { get; set; } = 10 * 100;

    public int CompensationOnlineThresholdSeconds { get; set; } = 60 * 60;

    public int CompensationSeconds { get; set; } = 60 * 60;

    // PacketBuilder.ServerTime advertises the original fixed UTC-8 clock.
    public int ServerUtcOffsetMinutes { get; set; } = -8 * 60;

    public int PersistenceIntervalSeconds { get; set; } = 30;

    public State.ZodiacEnergyPolicy Snapshot()
    {
        var policy = new State.ZodiacEnergyPolicy(
            Enabled,
            TickSeconds,
            BoostedDailySeconds,
            EmulatorBoostedEnergyPerTickX100,
            EmulatorNormalEnergyPerTickX100,
            CompensationOnlineThresholdSeconds,
            CompensationSeconds,
            ServerUtcOffsetMinutes);
        policy.Validate();
        return policy;
    }

    public void Normalize()
    {
        TickSeconds = Math.Max(1, TickSeconds);
        BoostedDailySeconds = Math.Max(0, BoostedDailySeconds);
        BoostedDailySeconds -= BoostedDailySeconds % TickSeconds;
        EmulatorBoostedEnergyPerTickX100 = Math.Max(0, EmulatorBoostedEnergyPerTickX100);
        EmulatorNormalEnergyPerTickX100 = Math.Max(0, EmulatorNormalEnergyPerTickX100);
        CompensationOnlineThresholdSeconds = Math.Max(0, CompensationOnlineThresholdSeconds);
        CompensationSeconds = Math.Max(0, CompensationSeconds);
        CompensationSeconds -= CompensationSeconds % TickSeconds;
        ServerUtcOffsetMinutes = Math.Clamp(ServerUtcOffsetMinutes, -14 * 60, 14 * 60);
        PersistenceIntervalSeconds = Math.Max(1, PersistenceIntervalSeconds);
    }
}

internal sealed class DeveloperCommandOptions
{
    public bool Enabled { get; set; }

    public int[] AllowedAccountIds { get; set; } = [];

    public bool Allows(int accountId)
    {
        return Enabled && accountId > 0 && (AllowedAccountIds ?? []).Contains(accountId);
    }
}

internal sealed class StorageOptions
{
    public string Provider { get; set; } = string.Empty;

    public string PostgresConnectionString { get; set; } = string.Empty;

    public PostgresOutboxDispatcherOptions Outbox { get; set; } = new();

    public CharacterCheckpointWorkerOptions Checkpoints { get; set; } =
        new();
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
