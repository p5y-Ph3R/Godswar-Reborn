using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ExplicitCharacterRealmInsertChecks
{
    public const string CheckName =
        "Explicit realm on post-migration character inserts";

    private static readonly Regex CharacterInsert = CharacterInsertRegex();

    private static readonly HashSet<string> HistoricalMigrationFixtures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresCharacterLifecycleMigrationIntegrationChecks.Database.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresCharacterLifecycleMigrationIntegrationChecks.Preflight.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetGrowthV2MigrationIntegrationChecks.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetGrowthSavvySemanticsV2MigrationIntegrationChecks.Database.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetHatchEvidenceHardeningIntegrationChecks.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetInitialSavvyMigrationIntegrationChecks.Fixtures.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetInitialSavvyV3MigrationIntegrationChecks.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetLevelMigrationIntegrationChecks.Fixtures.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetPhoenixGrowthMigrationIntegrationChecks.Database.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetRankContentMigrationIntegrationChecks.cs",
            "tests/Godswar.Server.ProtocolChecks/" +
                "PostgresPetScaledAddedValueMigrationIntegrationChecks.Database.cs"
        };

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var missing = new List<string>();
        foreach (var path in SourcePaths(root))
        {
            var relative = Path.GetRelativePath(root, path)
                .Replace('\\', '/');
            var source = File.ReadAllText(path);
            foreach (Match match in CharacterInsert.Matches(source))
            {
                if (HasServerId(match.Groups["columns"].Value) ||
                    HistoricalMigrationFixtures.Contains(relative))
                {
                    continue;
                }

                var line = source.Take(match.Index)
                    .Count(static value => value == '\n') + 1;
                missing.Add($"{relative}:{line}");
            }
        }

        Check.True(
            missing.Count == 0,
            "every post-migration character insert supplies server_id: " +
            string.Join(", ", missing));
        return Task.CompletedTask;
    }

    private static bool HasServerId(string columns) =>
        columns.Split(',').Any(static column =>
            string.Equals(
                column.Trim(),
                "server_id",
                StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SourcePaths(string root) =>
        new[] { "src", "tests", "database", "tools" }
            .Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists)
            .SelectMany(static directory =>
                Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories))
            .Where(static path =>
                Path.GetExtension(path) is ".cs" or ".sql" or ".ps1" &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin" +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj" +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "Godswar.Server",
                    "Godswar.Server.csproj")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Godswar repository root.");
    }

    [GeneratedRegex(
        "INSERT\\s+INTO\\s+(?:public\\.)?character_base\\s*\\(" +
        "(?<columns>.*?)\\)\\s*" +
        "(?:OVERRIDING\\s+SYSTEM\\s+VALUE\\s*)?(?:VALUES|SELECT)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant)]
    private static partial Regex CharacterInsertRegex();
}
