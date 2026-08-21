namespace Godswar.Server.ProtocolChecks;

internal static class HolySuitRealmAuthorityChecks
{
    public const string CheckName =
        "Realm-scoped Holy Suit daily quota authority";

    public static Task RunAsync()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Inventory");
        var executorSources = Directory
            .EnumerateFiles(
                directory,
                "PostgresHolySuitCommandExecutor*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .ToArray();
        var aggregate = string.Join('\n', executorSources);

        Check.True(
            !aggregate.Contains("TempestRealmId", StringComparison.Ordinal) &&
            !aggregate.Contains("RealmId.Tempest", StringComparison.Ordinal) &&
            !aggregate.Contains("realm_id = 1", StringComparison.Ordinal) &&
            !aggregate.Contains("realm_id=1", StringComparison.Ordinal),
            "Holy Suit quota runtime has no fixed Tempest authority");
        Check.True(
            aggregate.Contains("server_id", StringComparison.Ordinal) &&
            aggregate.Contains("character.Value.RealmId", StringComparison.Ordinal) &&
            aggregate.Contains("usage.realm_id = cb.server_id", StringComparison.Ordinal) &&
            aggregate.Contains("realmId.Value", StringComparison.Ordinal),
            "locked character realm flows through quota read, lock, and mutation");

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
}
