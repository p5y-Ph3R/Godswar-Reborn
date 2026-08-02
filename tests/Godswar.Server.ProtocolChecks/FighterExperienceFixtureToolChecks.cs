namespace Godswar.Server.ProtocolChecks;

internal static class FighterExperienceFixtureToolChecks
{
    public const string CheckName =
        "Offline unsigned fighter-EXP fixture safety";

    public static Task RunAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var path = Path.Combine(
            repositoryRoot,
            "tools",
            "SetLocalDevelopmentFighterExperience.ps1");
        var script = File.ReadAllText(path);
        AssertContains(
            script,
            "[uint32]::MaxValue",
            "GODSWAR_RUNTIME_PROFILE=LocalDevelopment",
            "Refusing to change fighter EXP while server container",
            "checkpoint_owner_id",
            "fighter_job_exp is not bigint",
            "fighter-level sealed",
            "BEGIN ISOLATION LEVEL SERIALIZABLE",
            "fighter_experience_fixture",
            "retention_policy",
            "'permanent'");
        return Task.CompletedTask;
    }

    private static void AssertContains(string value, params string[] parts)
    {
        foreach (var part in parts)
        {
            Check.True(
                value.Contains(part, StringComparison.Ordinal),
                $"fighter EXP fixture contains '{part}'");
        }
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
