namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static void CheckDamageAdapterSource()
    {
        var root = FindTrainingDummySkillRepositoryRoot();
        var game = Path.Combine(root, "src", "Godswar.Server", "Game");
        var production = Directory.GetFiles(game, "*.cs")
            .Select(File.ReadAllText)
            .ToArray();
        var combined = string.Join('\n', production);
        Check.True(
            File.Exists(Path.Combine(
                game,
                "TrainingDummyDamageSkillPolicy.cs")) &&
            File.Exists(Path.Combine(
                game,
                "GameClientHandler.TrainingDummyDamageSkills.cs")) &&
            File.Exists(Path.Combine(
                game,
                "GameSessionRegistry.TrainingDummyDamageSkills.cs")) &&
            !File.Exists(Path.Combine(
                game,
                "TrainingDummyChampionSkillPolicy.cs")) &&
            !File.Exists(Path.Combine(
                game,
                "GameClientHandler.TrainingDummyChampionSkills.cs")) &&
            !File.Exists(Path.Combine(
                game,
                "GameSessionRegistry.TrainingDummySkills.cs")),
            "the production adapter and scalar routing use generic damage-skill names");
        Check.True(
            !combined.Contains(
                "TrainingDummyChampionSkillPolicy",
                StringComparison.Ordinal) &&
            !combined.Contains(
                "ResolveTrainingDummyChampion",
                StringComparison.Ordinal) &&
            !combined.Contains(
                "AttackerIsNotChampion",
                StringComparison.Ordinal) &&
            !combined.Contains(
                "attacker.Character.Profession != 1",
                StringComparison.Ordinal),
            "no stale Champion-only adapter symbol or profession guard remains");

        var docs = Path.Combine(root, "docs");
        var currentDoc = Path.Combine(
            docs,
            "training-dummy-damage-skills-adapter.md");
        Check.True(
            File.Exists(currentDoc) &&
            !File.Exists(Path.Combine(
                docs,
                "training-dummy-champion-skills-adapter.md")) &&
            File.ReadAllText(currentDoc).StartsWith(
                "# Training-dummy damage-skill adapter",
                StringComparison.Ordinal),
            "the adapter documentation is profession-neutral while retaining class-specific matrices");
    }

    private static string FindTrainingDummySkillRepositoryRoot()
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
