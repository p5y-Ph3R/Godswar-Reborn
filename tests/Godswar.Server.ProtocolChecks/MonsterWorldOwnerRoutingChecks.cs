namespace Godswar.Server.ProtocolChecks;

internal static class MonsterWorldOwnerRoutingChecks
{
    public const string CheckName =
        "B18B monster world owner-routing ratchet";

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var attacks = Normalize(
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "Godswar.Server",
                "Game",
                "GameSessionRegistry.MonsterAttacks.cs")));
        var ecsAttacks = Normalize(
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "Godswar.Server",
                "Game",
                "GameSessionRegistry.MonsterAttacksEcs.cs")));
        var world = Normalize(
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "Godswar.Server",
                "Game",
                "GameSessionRegistry.MonsterWorld.cs")));
        var routing = Normalize(
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "Godswar.Server",
                "Game",
                "GameSessionRegistry.WorldRouting.cs")));

        Check.True(
            attacks.Contains(
                "ProcessMonsterAttackAsync(\n" +
                "        WorldInstanceRuntime runtime,",
                StringComparison.Ordinal),
            "monster attacks retain explicit world-instance identity");
        Check.True(
            ecsAttacks.Contains(
                "ProcessMonsterAttackEcsAsync(\n" +
                "        WorldInstanceRuntime runtime,",
                StringComparison.Ordinal),
            "ECS monster attacks retain explicit world-instance identity");
        Check.True(
            !attacks.Contains(
                "var statusContext = map.Snapshot()",
                StringComparison.Ordinal) &&
            !attacks.Contains(
                "foreach (var observer in map.Snapshot())",
                StringComparison.Ordinal) &&
            !ecsAttacks.Contains(
                "var statusContext = map.Snapshot()",
                StringComparison.Ordinal) &&
            !ecsAttacks.Contains(
                "foreach (var observer in map.Snapshot())",
                StringComparison.Ordinal),
            "attack member reads do not bypass the instance owner");
        Check.True(
            Count(attacks, "SnapshotMonsterAttackMembers(runtime)") >= 2 &&
            Count(ecsAttacks, "SnapshotMonsterAttackMembers(runtime)") >= 2,
            "target and observer snapshots flow through the owner helper");
        Check.True(
            Count(attacks, "ClearMonsterAttackAggro(") >= 3 &&
            Count(ecsAttacks, "ClearMonsterAttackAggro(") >= 3,
            "legacy and ECS aggro mutations flow through the owner helper");
        Check.True(
            world.Contains(
                "await ProcessMonsterAttackAsync(\n" +
                "                            worldTick.Runtime,",
                StringComparison.Ordinal),
            "monster tick attacks pass their exact runtime");
        Check.True(
            world.Contains(
                "ownedMap.TryGetMonsterSnapshot(",
                StringComparison.Ordinal) &&
            !world.Contains(
                "!map.TryGetMonsterSnapshot(monster.ObjectId",
                StringComparison.Ordinal),
            "late movement freshness reads execute through the owner");
        Check.True(
            routing.Contains(
                "current.WorldInstanceId ==\n" +
                "                   recipientSnapshot.WorldInstanceId",
                StringComparison.Ordinal) &&
            routing.Contains(
                "current.WorldRevision ==\n" +
                "                   recipientSnapshot.WorldRevision",
                StringComparison.Ordinal),
            "fanout revalidates captured instance identity and revision");
        Check.True(
            !attacks.Contains(
                "targetContext.Session.SendAsync(",
                StringComparison.Ordinal) &&
            !attacks.Contains(
                "observer.Session.SendAsync(",
                StringComparison.Ordinal) &&
            !ecsAttacks.Contains(
                "targetContext.Session.SendAsync(",
                StringComparison.Ordinal) &&
            !ecsAttacks.Contains(
                "observer.Session.SendAsync(",
                StringComparison.Ordinal),
            "monster attack self and observer sends use revalidated egress");

        return Task.CompletedTask;
    }

    private static int Count(
        string source,
        string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory =
                 new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root.");
    }
}
