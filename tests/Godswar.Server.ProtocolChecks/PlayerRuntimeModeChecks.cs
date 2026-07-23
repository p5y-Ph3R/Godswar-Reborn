using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerRuntimeModeChecks
{
    private const string EnvironmentKey = "GODSWAR_PLAYER_RUNTIME";

    public static void Run()
    {
        Check.True(
            new GameSessionRegistry().PlayerRuntimeMode ==
                PlayerRuntimeMode.Ecs,
            "player runtime defaults to the parity-gated ECS path");
        Check.True(
            new GameSessionRegistry(
                null,
                null,
                MonsterRuntimeMode.Ecs,
                PlayerRuntimeMode.Legacy).PlayerRuntimeMode ==
                PlayerRuntimeMode.Legacy,
            "player legacy runtime remains an explicit rollback path");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new GameSessionRegistry(
                null,
                null,
                MonsterRuntimeMode.Legacy,
                (PlayerRuntimeMode)99),
            "unknown player runtime mode is rejected");
        CheckConfigurationBinding();
    }

    private static void CheckConfigurationBinding()
    {
        var previous =
            Environment.GetEnvironmentVariable(EnvironmentKey);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-player-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, null);
            var legacyPath = WriteOptions(
                directory,
                "legacy.json",
                "Legacy");
            var ecsPath = WriteOptions(directory, "ecs.json", "Ecs");
            Check.True(
                ServerOptions.Load(legacyPath).Game.Players.Runtime ==
                    PlayerRuntimeMode.Legacy,
                "JSON legacy player runtime");
            Check.True(
                ServerOptions.Load(ecsPath).Game.Players.Runtime ==
                    PlayerRuntimeMode.Ecs,
                "JSON ECS player runtime");

            Environment.SetEnvironmentVariable(EnvironmentKey, "ecs");
            Check.True(
                ServerOptions.Load(legacyPath).Game.Players.Runtime ==
                    PlayerRuntimeMode.Ecs,
                "environment player runtime override");

            Environment.SetEnvironmentVariable(EnvironmentKey, "unknown");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(legacyPath),
                "invalid environment player runtime fails startup");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteOptions(
        string directory,
        string fileName,
        string runtime)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(
            path,
            $$"""
            {
              "game": {
                "players": {
                  "runtime": "{{runtime}}"
                }
              }
            }
            """);
        return path;
    }
}
