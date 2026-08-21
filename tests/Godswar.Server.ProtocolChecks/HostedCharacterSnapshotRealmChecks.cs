using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HostedCharacterSnapshotRealmChecks
{
    public const string CheckName =
        "Hosted character snapshot reads require the process realm";

    private static readonly Regex SnapshotRead = SnapshotReadRegex();

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var gameDirectory = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game");
        var implicitReads = new List<string>();
        var readCount = 0;

        foreach (var path in Directory.EnumerateFiles(
                     gameDirectory,
                     "GameClientHandler*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(path);
            foreach (Match match in SnapshotRead.Matches(source))
            {
                readCount++;
                var arguments = match.Groups["arguments"].Value
                    .Split(',', StringSplitOptions.TrimEntries);
                if (arguments.Length >= 3 &&
                    string.Equals(
                        arguments[1],
                        "_processRealmId",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var line = source.Take(match.Index)
                    .Count(static value => value == '\n') + 1;
                var relative = Path.GetRelativePath(root, path)
                    .Replace('\\', '/');
                implicitReads.Add($"{relative}:{line}");
            }
        }

        Check.True(
            readCount > 0,
            "hosted character snapshot reads were discovered");
        Check.True(
            implicitReads.Count == 0,
            "every hosted character snapshot read supplies _processRealmId: " +
            string.Join(", ", implicitReads));
        return Task.CompletedTask;
    }

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
        "_characterSnapshots\\s*\\.\\s*ReadAsync\\s*\\(" +
        "(?<arguments>.*?)\\)\\s*;",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotReadRegex();
}
