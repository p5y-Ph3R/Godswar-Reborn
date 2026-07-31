namespace Godswar.Server.ProtocolChecks;

internal static partial class ServerRuntimeProfileChecks
{
    private const string GamePortEnvironment =
        "GODSWAR_GAME_PORT";
    private const string GamePublicPortEnvironment =
        "GODSWAR_GAME_PUBLIC_PORT";

    private static void CheckGamePublicEndpointConfiguration()
    {
        var previousGamePort = Environment.GetEnvironmentVariable(
            GamePortEnvironment);
        var previousPublicPort = Environment.GetEnvironmentVariable(
            GamePublicPortEnvironment);
        var directory = NewTemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable(
                GamePortEnvironment,
                null);
            Environment.SetEnvironmentVariable(
                GamePublicPortEnvironment,
                null);

            var fallbackPath = WriteGameEndpointOptions(
                directory,
                gamePort: 7_100,
                publicPort: 0);
            var fallback = ServerOptions.Load(fallbackPath);
            Check.Equal(
                7_100,
                fallback.Game.PublicPort,
                "zero public port follows the game listener");

            var explicitPath = WriteGameEndpointOptions(
                directory,
                gamePort: 7_100,
                publicPort: 7_200);
            var explicitOptions = ServerOptions.Load(explicitPath);
            Check.Equal(
                7_200,
                explicitOptions.Game.PublicPort,
                "JSON public port is independent from the listener");

            Environment.SetEnvironmentVariable(
                GamePublicPortEnvironment,
                "7300");
            var environment = ServerOptions.Load(explicitPath);
            Check.Equal(
                7_300,
                environment.Game.PublicPort,
                "environment overrides the public game port");

            Environment.SetEnvironmentVariable(
                GamePublicPortEnvironment,
                "not-a-port");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(explicitPath),
                "malformed public game port fails startup");

            Environment.SetEnvironmentVariable(
                GamePublicPortEnvironment,
                "65536");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(explicitPath),
                "out-of-range public game port fails startup");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GamePortEnvironment,
                previousGamePort);
            Environment.SetEnvironmentVariable(
                GamePublicPortEnvironment,
                previousPublicPort);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteGameEndpointOptions(
        string directory,
        int gamePort,
        int publicPort)
    {
        var path = Path.Combine(
            directory,
            $"appsettings-{gamePort}-{publicPort}.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "runtimeProfile": "LocalDevelopment",
              "storage": {
                "provider": "Json"
              },
              "authentication": {
                "allowLegacyRawAuthentication": true
              },
              "game": {
                "port": {{gamePort}},
                "publicPort": {{publicPort}}
              }
            }
            """);
        return path;
    }
}
