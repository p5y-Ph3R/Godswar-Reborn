using System.Text.Json;

namespace Godswar.Server;

internal sealed class ServerOptions
{
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

    public static ServerOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new ServerOptions();
            var json = JsonSerializer.Serialize(defaults, JsonDefaults.Indented);
            File.WriteAllText(path, json);
            return defaults.ApplyEnvironment().Normalize(path);
        }

        var options = JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), JsonDefaults.Indented)
            ?? new ServerOptions();

        return options.ApplyEnvironment().Normalize(path);
    }

    private ServerOptions ApplyEnvironment()
    {
        Login.BindHost = Environment.GetEnvironmentVariable("GODSWAR_LOGIN_BIND_HOST") ?? Login.BindHost;
        Login.Port = ReadInt("GODSWAR_LOGIN_PORT", Login.Port);
        Game.BindHost = Environment.GetEnvironmentVariable("GODSWAR_GAME_BIND_HOST") ?? Game.BindHost;
        Game.Port = ReadInt("GODSWAR_GAME_PORT", Game.Port);
        Game.PublicHost = Environment.GetEnvironmentVariable("GODSWAR_GAME_PUBLIC_HOST") ?? Game.PublicHost;
        DataPath = Environment.GetEnvironmentVariable("GODSWAR_DATA_PATH") ?? DataPath;
        Storage.Provider = Environment.GetEnvironmentVariable("GODSWAR_STORAGE_PROVIDER") ?? Storage.Provider;
        Storage.PostgresConnectionString = Environment.GetEnvironmentVariable("GODSWAR_POSTGRES_CONNECTION_STRING")
            ?? Storage.PostgresConnectionString;

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

        if (string.IsNullOrWhiteSpace(Storage.Provider))
        {
            Storage.Provider = "json";
        }

        return this;
    }

    private static int ReadInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
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
}

internal sealed class StorageOptions
{
    public string Provider { get; set; } = "json";

    public string PostgresConnectionString { get; set; } = string.Empty;
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
