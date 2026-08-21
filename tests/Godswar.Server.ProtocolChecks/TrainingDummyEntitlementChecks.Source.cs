namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyEntitlementChecks
{
    private static void CheckFixtureSourceContracts()
    {
        var root = FindRepositoryRoot();
        var identity = File.ReadAllText(Path.Combine(
            root,
            "database",
            "fixtures",
            "max-combat-characters",
            "01_identity.sql"));
        AssertContains(
            identity,
            "0,1,0,148,-154,'warrior')",
            "1,1,0,148,-162,'champion_dodge')",
            "0,0,1,148,-154,'warrior')",
            "1,0,1,148,-162,'champion_dodge')");

        var status = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "ProvisionLocalDevelopmentMaxCombatFixture.Status.ps1"));
        AssertContains(
            status,
            "'AresBulwark',0,1,0,148::real,-154::real",
            "'AresMirage',1,1,0,148::real,-162::real",
            "'AthenaBulwark',0,0,1,148::real,-154::real",
            "'AthenaMirage',1,0,1,148::real,-162::real",
            "'AresTempest',1,0,0,136::real,-150::real");
        const string ownerMergeRevision =
            "3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E";
        AssertContains(
            status,
            "(29,122058)",
            "(30,101695)",
            "(38,60879)",
            "(1001,3000)",
            "(1002,3000)",
            ownerMergeRevision);
        var pets = File.ReadAllText(Path.Combine(
            root,
            "database",
            "fixtures",
            "max-combat-characters",
            "04_pets.sql"));
        AssertContains(
            pets,
            "(29,122058)",
            "(30,101695)",
            "(38,60879)",
            "(1001,3000)",
            "(1002,3000)",
            ownerMergeRevision);
        Check.True(
            !status.Contains("(38,76061.25)", StringComparison.Ordinal) &&
            !pets.Contains("(38,76061.25)", StringComparison.Ordinal) &&
            !status.Contains("(29,61029)", StringComparison.Ordinal) &&
            !pets.Contains("(29,61029)", StringComparison.Ordinal) &&
            !status.Contains("(30,50847.5)", StringComparison.Ordinal) &&
            !pets.Contains("(30,50847.5)", StringComparison.Ordinal) &&
            !status.Contains(
                "EEA02574B39EDED6DBEFCACF80337AAE0166A44366115AB7E8360DD39B36C84D",
                StringComparison.Ordinal) &&
            !pets.Contains(
                "EEA02574B39EDED6DBEFCACF80337AAE0166A44366115AB7E8360DD39B36C84D",
                StringComparison.Ordinal) &&
            !status.Contains(
                "E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929",
                StringComparison.Ordinal) &&
            !pets.Contains(
                "E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929",
                StringComparison.Ordinal),
            "max-combat source no longer pins Agility-derived rebound");

        const string pin =
            "OR (i.expected_id BETWEEN 7001 AND 7004 AND";
        const string positionX =
            "c.\"Pos_X\" IS DISTINCT FROM i.pos_x";
        const string positionZ =
            "c.\"Pos_Z\" IS DISTINCT FROM i.pos_z";
        var pinIndex = status.IndexOf(pin, StringComparison.Ordinal);
        var positionXIndex = status.IndexOf(
            positionX,
            StringComparison.Ordinal);
        var positionZIndex = status.IndexOf(
            positionZ,
            StringComparison.Ordinal);
        var nextIdentityField = status.IndexOf(
            "OR c.\"Money\"",
            StringComparison.Ordinal);
        Check.True(
            pinIndex >= 0 &&
            positionXIndex > pinIndex &&
            positionZIndex > positionXIndex &&
            nextIdentityField > positionZIndex &&
            Count(status, positionX) == 1 &&
            Count(status, positionZ) == 1,
            "status pins only the four host-owned dummy positions");

        CheckSpawnPkModeCoverage(root);
    }

    private static void CheckSpawnPkModeCoverage(string root)
    {
        var gameDirectory = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game");
        var visibility = File.ReadAllText(Path.Combine(
            gameDirectory,
            "GameClientHandler.PlayerVisibility.cs"));
        var broadcasts = File.ReadAllText(Path.Combine(
            gameDirectory,
            "GameClientHandler.WorldBroadcast.cs"));
        var spawnCount = Count(
            visibility,
            "PacketBuilder.PlayerWorldSpawn(") +
            Count(
                broadcasts,
                "PacketBuilder.PlayerWorldSpawn(");
        var projectionCount = Count(
            visibility,
            "pkMode: _registry.TrainingDummySpawnPkMode(") +
            Count(
                broadcasts,
                "pkMode: _registry.TrainingDummySpawnPkMode(");
        Check.True(
            spawnCount == 3 && projectionCount == spawnCount,
            "all player-spawn egresses use the exact-dummy PK-mode projection");

        CheckTrainingDummyPreSpawnReset(
            broadcasts,
            visibility);
    }

    private static void CheckTrainingDummyPreSpawnReset(
        string broadcasts,
        string visibility)
    {
        const string guard =
            "if (_registry.IsTrainingDummy(_character))";
        const string reason =
            "\"TrainingDummyPreSpawnReset\"";
        const string expectedAdjacentSource =
            "if(_registry.IsTrainingDummy(_character))" +
            "{await_registry.BroadcastToMapAsync(" +
            "_character.CurrentMap," +
            "PacketBuilder.RemoveWorldObjects(objectId)," +
            "cancellationToken,_session," +
            "\"TrainingDummyPreSpawnReset\");}" +
            "varspawnRecipients=await_registry.BroadcastToMapAsync(";
        var compactBroadcasts = string.Concat(
            broadcasts.Where(character => !char.IsWhiteSpace(character)));
        Check.True(
            compactBroadcasts.Contains(
                expectedAdjacentSource,
                StringComparison.Ordinal) &&
            Count(broadcasts, guard) == 1 &&
            Count(broadcasts, reason) == 1 &&
            Count(visibility, reason) == 0,
            "exact dummies receive one awaited remove immediately before their current-player announcement spawn");
    }

    private static void AssertContains(
        string source,
        params string[] expected)
    {
        foreach (var value in expected)
        {
            Check.True(
                source.Contains(value, StringComparison.Ordinal),
                $"fixture source contains '{value}'");
        }
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(
            value,
            index,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
