using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Repository ratchet for the accepted B16 decision to defer Redis while the
/// secure runtime remains single-process.
/// </summary>
internal static partial class DeferredRedisArchitectureChecks
{
    public const string CheckName =
        "Deferred Redis architecture ratchet";

    private const string DecisionGate =
        "ADR 0003 must be deliberately superseded before adding Redis";

    public static Task RunAsync()
    {
        var repositoryRoot = FindRepositoryRoot();

        CheckServerHasNoRedisClient(repositoryRoot);
        CheckComposeHasNoRedisRuntime(repositoryRoot);
        CheckSingleProcessTicketComposition(repositoryRoot);
        CheckRedisRuntimeDirectoryIsAbsent(repositoryRoot);

        return Task.CompletedTask;
    }

    private static void CheckServerHasNoRedisClient(string repositoryRoot)
    {
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server",
            "Godswar.Server.csproj");
        var project = XDocument.Load(projectPath);
        var redisReferences = project
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "PackageReference")
            .Select(element =>
                element.Attribute("Include")?.Value ??
                element.Attribute("Update")?.Value)
            .Where(package =>
                package?.Contains(
                    "Redis",
                    StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        Check.Equal(
            0,
            redisReferences.Length,
            $"server has no Redis client PackageReference; {DecisionGate}");
    }

    private static void CheckComposeHasNoRedisRuntime(
        string repositoryRoot)
    {
        var composePaths = Directory
            .EnumerateFiles(
                repositoryRoot,
                "docker-compose*.yml",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                repositoryRoot,
                "docker-compose*.yaml",
                SearchOption.TopDirectoryOnly))
            .OrderBy(
                static path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Check.True(
            composePaths.Length > 0,
            "repository retains at least one Docker Compose definition");

        foreach (var composePath in composePaths)
        {
            var compose = File.ReadAllText(composePath);
            var relativePath = Path.GetRelativePath(
                repositoryRoot,
                composePath);

            Check.True(
                !RedisServicePattern().IsMatch(compose),
                $"{relativePath} has no Redis service; {DecisionGate}");
            Check.True(
                !RedisImagePattern().IsMatch(compose),
                $"{relativePath} has no Redis image; {DecisionGate}");
        }
    }

    private static void CheckSingleProcessTicketComposition(
        string repositoryRoot)
    {
        var programPath = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server",
            "Program.cs");
        var program = File.ReadAllText(programPath);

        AssertOrdered(
            program,
            "single-process secure ticket composition",
            "using InMemoryGameTicketStore? secureGameTickets",
            "options.Secure.Enabled",
            "? new InMemoryGameTicketStore(",
            "options.Secure.Tickets.Capacity",
            "options.Secure.Tickets.Ttl");
    }

    private static void CheckRedisRuntimeDirectoryIsAbsent(
        string repositoryRoot)
    {
        var redisRuntimePath = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Redis");

        Check.True(
            !Directory.Exists(redisRuntimePath),
            $"Redis runtime directory remains absent; {DecisionGate}");
    }

    private static void AssertOrdered(
        string source,
        string subject,
        params string[] tokens)
    {
        var priorIndex = -1;
        foreach (var token in tokens)
        {
            var index = source.IndexOf(
                token,
                priorIndex + 1,
                StringComparison.Ordinal);
            Check.True(
                index > priorIndex,
                $"{subject} retains ordered token '{token}'; " +
                DecisionGate);
            priorIndex = index;
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var current = new DirectoryInfo(start);
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
        }

        throw new InvalidOperationException(
            "Could not locate the repository root.");
    }

    [GeneratedRegex(
        @"^[ \t]+redis\s*:",
        RegexOptions.IgnoreCase |
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant)]
    private static partial Regex RedisServicePattern();

    [GeneratedRegex(
        @"^\s*image\s*:\s*[^\r\n#]*redis",
        RegexOptions.IgnoreCase |
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant)]
    private static partial Regex RedisImagePattern();
}
