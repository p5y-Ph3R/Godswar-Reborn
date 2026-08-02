namespace Godswar.Server.ProtocolChecks;

internal static class FighterLevelSealToolChecks
{
    public const string CheckName =
        "Offline LocalDevelopment fighter-level seal tool";

    public static async Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(
            root,
            "tools",
            "SetLocalDevelopmentFighterLevelSeal.ps1"));
        var documentation = await File.ReadAllTextAsync(Path.Combine(
            root,
            "docs",
            "fighter-level-sealing.md"));

        Check.True(
            script.Contains(
                "ValidatePattern('^[A-Za-z0-9_]{1,32}$')",
                StringComparison.Ordinal) &&
            script.Contains(
                "$CharacterName -notmatch '^[A-Za-z0-9_]{1,32}$'",
                StringComparison.Ordinal),
            "character identity is conservatively bounded before SQL interpolation");
        Check.True(
            script.Contains(
                "GODSWAR_RUNTIME_PROFILE=LocalDevelopment",
                StringComparison.Ordinal) &&
            script.Contains("$serverState.Running", StringComparison.Ordinal),
            "tool requires an explicit LocalDevelopment profile and stopped server");
        Check.True(
            script.Contains("checkpoint_owner_id", StringComparison.Ordinal) &&
            script.Contains(
                "v_checkpoint_owner IS NOT NULL",
                StringComparison.Ordinal),
            "tool refuses a character with a checkpoint owner");
        Check.True(
            script.Contains(
                "IF $desiredSql AND v_level <> 89",
                StringComparison.Ordinal) &&
            script.Contains(
                "fighter_level_sealed IS DISTINCT FROM $desiredSql",
                StringComparison.Ordinal),
            "tool seals only exact level 89 and changes state idempotently");
        Check.True(
            script.Contains(
                "INSERT INTO public.command_audit",
                StringComparison.Ordinal) &&
            script.Contains("'permanent'", StringComparison.Ordinal) &&
            script.Contains("'goldCharged', 0", StringComparison.Ordinal),
            "every fixture invocation records a permanent no-Gold audit");
        Check.True(
            !script.Contains("character_wallet", StringComparison.OrdinalIgnoreCase) &&
            !script.Contains("SET gold", StringComparison.OrdinalIgnoreCase) &&
            documentation.Contains(
                "does not advance `progression_reward_revision`",
                StringComparison.Ordinal) &&
            documentation.Contains(
                "does not charge Gold",
                StringComparison.Ordinal),
            "offline no-Gold and unchanged progression-revision scope is explicit");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
