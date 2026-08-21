namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerWorldObjectIdChecks
{
    private static void CheckNoProductionRecomputation()
    {
        var root = FindPlayerObjectIdRepositoryRoot();
        var gameDirectory = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game");
        var allocatorFiles = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(
                gameDirectory,
                "WorldObjectIds.cs")),
            Path.GetFullPath(Path.Combine(
                gameDirectory,
                "GameSessionRegistry.PlayerObjectIds.cs"))
        };
        var generatedDirectoryToken =
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(root, "src", "Godswar.Server"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path =>
                !allocatorFiles.Contains(path) &&
                !path.Contains(
                    generatedDirectoryToken,
                    StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllText(path).Contains(
                    "WorldObjectIds.ForPlayer(",
                    StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Check.True(
            offenders.Length == 0,
            "production player-object egress uses assigned session IDs; " +
            $"formula recomputation remains in: {string.Join(", ", offenders)}");

        CheckReplacementRemovalSource(root);
    }

    private static void CheckReplacementRemovalSource(string root)
    {
        var gameDirectory = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game");
        var login = File.ReadAllText(Path.Combine(
            gameDirectory,
            "GameClientHandler.LoginWorldEntry.cs"));
        var replaceIndex = login.IndexOf(
            "ReplaceAccountSessionAndDetachWorld(",
            StringComparison.Ordinal);
        var publishIndex = login.IndexOf(
            "await PublishReplacedWorldRemovalAsync(",
            StringComparison.Ordinal);
        Check.True(
            replaceIndex >= 0 && publishIndex > replaceIndex,
            "login detaches the replaced world before publishing its removal");

        var helper = File.ReadAllText(Path.Combine(
            gameDirectory,
            "GameClientHandler.AccountSessionReplacement.cs"));
        var broadcastIndex = helper.IndexOf(
            "BroadcastToWorldInstanceAsync(",
            StringComparison.Ordinal);
        var instanceIndex = IndexAfter(
            helper,
            "context.WorldInstanceId",
            broadcastIndex);
        var removalIndex = IndexAfter(
            helper,
            "PacketBuilder.RemoveWorldObjects(",
            instanceIndex);
        var objectIndex = IndexAfter(
            helper,
            "context.ObjectId",
            removalIndex);
        var finallyIndex = IndexAfter(
            helper,
            "finally",
            objectIndex);
        var releaseIndex = IndexAfter(
            helper,
            "ReleaseDetachedPlayerWorld(detached)",
            finallyIndex);
        Check.True(
            broadcastIndex >= 0 &&
            instanceIndex > broadcastIndex &&
            removalIndex > instanceIndex &&
            objectIndex > removalIndex &&
            finallyIndex > objectIndex &&
            releaseIndex > finallyIndex,
            "replacement removal targets the detached instance/object and " +
            "always releases its reserved ID afterward");
    }

    private static int IndexAfter(
        string source,
        string value,
        int predecessor) =>
        predecessor < 0
            ? -1
            : source.IndexOf(
                value,
                predecessor,
                StringComparison.Ordinal);

    private static string FindPlayerObjectIdRepositoryRoot()
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

        throw new InvalidOperationException(
            "Could not locate the repository root for object-ID checks.");
    }
}
