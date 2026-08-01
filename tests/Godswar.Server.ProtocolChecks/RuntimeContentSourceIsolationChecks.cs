using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RuntimeContentSourceIsolationChecks
{
    public const string CheckName =
        "Process-pinned runtime content source isolation";

    private static readonly string[] MutableSources =
    [
        "item_templates",
        "item_attribute_templates",
        "equipment_rank_rules",
        "holy_suit_effect_templates",
        "class_templates",
        "talent_effect_templates",
        "talent_templates",
        "skill_templates",
        "skill_book_templates",
        "map_templates",
        "map_address_points",
        "map_links",
        "monster_templates",
        "world_boss_areas",
        "pending_world_boss_areas",
        "npc_text_templates",
        "npc_appearance_templates",
        "npc_spawn_references",
        "npc_function_templates",
        "npc_dialog_templates",
        "pet_templates",
        "pet_aptitude_templates"
    ];

    private static readonly HashSet<string> ApprovedBoundaries = new(
        StringComparer.Ordinal)
    {
        "src/Godswar.Server/Infrastructure/Database/" +
            "PostgresRelationalContentBaselineBootstrapper.cs",
        "src/Godswar.Server/Infrastructure/Database/" +
            "PostgresRelationalContentBaselineBootstrapper.Policy.cs",
        "src/Godswar.Server/Infrastructure/Items/" +
            "PostgresItemTemplateBaselinePublisher.cs",
        "src/Godswar.Server/Infrastructure/Items/" +
            "PostgresItemTemplateBaselinePublisher.Policy.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresGameplayContentPublisher.Read.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresGameplayContentPublisher.Write.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresGameplayContentPublisher.ProgressionRead.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresGameplayContentPublisher.ProgressionWrite.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresMonsterContentBaselinePublisher.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresNpcContentBaselinePublisher.cs",
        "src/Godswar.Server/Infrastructure/WorldContent/" +
            "PostgresNpcDialogueBaselinePublisher.cs"
    };

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var serverRoot = Path.Combine(root, "src", "Godswar.Server");
        var runtimeFiles = Directory.EnumerateFiles(
                serverRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Select(path => new SourceFile(
                Relative(root, path),
                File.ReadAllText(path)))
            .Where(static file =>
                !file.Path.StartsWith(
                    "src/Godswar.Server/State/DatabaseMigrations/",
                    StringComparison.Ordinal) &&
                !ApprovedBoundaries.Contains(file.Path))
            .ToArray();

        var direct = runtimeFiles
            .Where(file => MutableReadPattern().IsMatch(file.Source))
            .Select(static file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check.True(
            direct.Length == 0,
            "mutable content sources have no runtime readers: " +
            string.Join(", ", direct));

        var taintedObjects = FindTaintedSchemaObjects(root);
        var indirect = runtimeFiles
            .Where(file => taintedObjects.Any(name =>
                SqlObjectReference(name).IsMatch(file.Source)))
            .Select(static file => file.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check.True(
            indirect.Length == 0,
            "runtime SQL cannot indirectly reach mutable content through " +
            "a view or function: " + string.Join(", ", indirect));
        return Task.CompletedTask;
    }

    private static HashSet<string> FindTaintedSchemaObjects(string root)
    {
        var definitions = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in SchemaDefinitionFiles(root))
        {
            var source = File.ReadAllText(path);
            AddDefinitions(definitions, source, ViewPattern());
            AddDefinitions(definitions, source, FunctionPattern());
        }

        var tainted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, body) in definitions)
            {
                if (tainted.Contains(name) ||
                    !MutableSources.Any(source =>
                        Token(source).IsMatch(body)) &&
                    !tainted.Any(source => Token(source).IsMatch(body)))
                {
                    continue;
                }

                changed |= tainted.Add(name);
            }
        }

        return tainted;
    }

    private static void AddDefinitions(
        IDictionary<string, string> destination,
        string source,
        Regex pattern)
    {
        foreach (Match match in pattern.Matches(source))
        {
            destination[match.Groups["name"].Value] =
                match.Groups["body"].Value;
        }
    }

    private static IEnumerable<string> SchemaDefinitionFiles(string root) =>
        Directory.EnumerateFiles(
                Path.Combine(
                    root,
                    "src",
                    "Godswar.Server",
                    "State",
                    "DatabaseMigrations"),
                "*",
                SearchOption.AllDirectories)
            .Concat(Directory.Exists(Path.Combine(root, "database"))
                ? Directory.EnumerateFiles(
                    Path.Combine(root, "database"),
                    "*.sql",
                    SearchOption.AllDirectories)
                : []);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static Regex MutableReadPattern() => new(
        @"\b(?:FROM|JOIN)\s+(?:public\.)?(?:" +
        string.Join("|", MutableSources.Select(Regex.Escape)) +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex ViewPattern() => new(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+" +
        @"(?:public\.)?(?<name>[a-z_][a-z0-9_]*)\s+AS\s+" +
        @"(?<body>.*?);",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant |
        RegexOptions.Singleline);

    private static Regex FunctionPattern() => new(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+" +
        @"(?:public\.)?(?<name>[a-z_][a-z0-9_]*)\b" +
        @"(?<body>.*?)(?:\$[a-z_0-9]*\$\s*;)",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant |
        RegexOptions.Singleline);

    private static Regex Token(string value) => new(
        $@"\b{Regex.Escape(value)}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex SqlObjectReference(string value) => new(
        @"(?:\b(?:FROM|JOIN|UPDATE|INSERT\s+INTO|DELETE\s+FROM)\s+" +
        $@"(?:public\.)?{Regex.Escape(value)}\b|" +
        $@"\b{Regex.Escape(value)}\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "GodswarServer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for content checks.");
    }

    private sealed record SourceFile(string Path, string Source);
}
