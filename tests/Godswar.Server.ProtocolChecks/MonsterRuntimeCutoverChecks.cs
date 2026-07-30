using Godswar.Server.Game;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterRuntimeCutoverChecks
{
    private const string EnvironmentKey = "GODSWAR_MONSTER_RUNTIME";

    public static Task RunAsync()
    {
        Check.True(
            new ServerOptions().Game.Monsters.Runtime ==
            MonsterRuntimeMode.Ecs,
            "source-default monster runtime is ECS");
        CheckRuntimeSelection(MonsterRuntimeMode.Legacy);
        CheckRuntimeSelection(MonsterRuntimeMode.Ecs);
        CheckConfigurationBinding();
        Check.Throws<ArgumentOutOfRangeException>(
            () => MonsterMapRuntimeFactory.Create(
                (MonsterRuntimeMode)int.MaxValue,
                mapId: 0,
                definitions: [],
                initializedAt: DateTimeOffset.UnixEpoch),
            "runtime factory rejects an unsupported mode");
        return Task.CompletedTask;
    }

    private static void CheckRuntimeSelection(MonsterRuntimeMode mode)
    {
        var map = new MapInstance(0, mode);
        var runtime = map.InitializeMonsters(
            [],
            DateTimeOffset.UnixEpoch);
        var expectedType = mode == MonsterRuntimeMode.Legacy
            ? typeof(MonsterMapRuntime)
            : typeof(EcsMonsterMapRuntime);

        Check.True(
            expectedType == runtime.GetType(),
            $"{mode} map runtime type");
        Check.True(
            ReferenceEquals(
                runtime,
                map.InitializeMonsters(
                    [],
                    DateTimeOffset.UnixEpoch.AddMinutes(1))),
            $"{mode} map runtime initializes once");
        Check.Equal(0, runtime.Count, $"{mode} empty runtime count");
    }

    private static void CheckConfigurationBinding()
    {
        var priorEnvironmentValue =
            Environment.GetEnvironmentVariable(EnvironmentKey);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-monster-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, null);
            var legacyPath = WriteOptions(directory, "legacy.json", "Legacy");
            var ecsPath = WriteOptions(directory, "ecs.json", "Ecs");
            Check.True(
                ServerOptions.Load(legacyPath).Game.Monsters.Runtime ==
                MonsterRuntimeMode.Legacy,
                "JSON legacy monster runtime");
            Check.True(
                ServerOptions.Load(ecsPath).Game.Monsters.Runtime ==
                MonsterRuntimeMode.Ecs,
                "JSON ECS monster runtime");

            Environment.SetEnvironmentVariable(EnvironmentKey, "ecs");
            Check.True(
                ServerOptions.Load(legacyPath).Game.Monsters.Runtime ==
                MonsterRuntimeMode.Ecs,
                "environment monster runtime override");

            Environment.SetEnvironmentVariable(EnvironmentKey, "unknown");
            Check.Throws<InvalidDataException>(
                () => ServerOptions.Load(legacyPath),
                "invalid environment monster runtime fails startup");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                EnvironmentKey,
                priorEnvironmentValue);
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
              "runtimeProfile": "LocalDevelopment",
              "storage": {
                "provider": "Json"
              },
              "authentication": {
                "allowLegacyRawAuthentication": true
              },
              "game": {
                "monsters": {
                  "runtime": "{{runtime}}"
                }
              }
            }
            """);
        return path;
    }
}
