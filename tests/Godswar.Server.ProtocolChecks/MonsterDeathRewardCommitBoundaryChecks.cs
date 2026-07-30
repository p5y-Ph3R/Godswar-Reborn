using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterDeathRewardCommitBoundaryChecks
{
    public static async Task RunAsync()
    {
        await CheckCommitPrecedesCancelledDeliveryAsync();
        CheckAllCombatPathsPrepareBeforeDelivery();
        CheckAreaPreparationAdvancesProjection();
    }

    private static async Task
        CheckCommitPrecedesCancelledDeliveryAsync()
    {
        var trace = new List<string>();
        var attempts = 0;
        var result = await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            cancellationToken =>
            {
                Check.True(
                    !cancellationToken.CanBeCanceled,
                    "monster reward commit ignores session cancellation");
                attempts++;
                trace.Add($"commit-{attempts}");
                return attempts == 1
                    ? Task.FromException<int>(
                        new IOException("unknown commit outcome"))
                    : Task.FromResult(42);
            },
            allowImmediateReplay: true);

        using var cancelledDelivery = new CancellationTokenSource();
        cancelledDelivery.Cancel();
        try
        {
            trace.Add("delivery");
            cancelledDelivery.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
        }

        Check.True(
            result == 42 &&
            attempts == 2 &&
            trace.SequenceEqual(
                ["commit-1", "commit-2", "delivery"]),
            "durable replay completes before a cancelled post-hit delivery");
    }

    private static void CheckAllCombatPathsPrepareBeforeDelivery()
    {
        var root = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     "src/Godswar.Server/Game/GameClientHandler.MovementCombat.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatEcsBasic.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatSkill.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatEcsSkill.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatArea.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatEcsArea.cs"
                 })
        {
            var source = File.ReadAllText(
                Path.Combine(
                    root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prepare = source.IndexOf(
                "PrepareMonsterKillRewardAsync",
                StringComparison.Ordinal);
            var publish = source.IndexOf(
                "PublishMonsterKillRewardAsync",
                StringComparison.Ordinal);
            Check.True(
                prepare >= 0 &&
                publish > prepare &&
                !source.Contains(
                    "AwardMonsterKillAsync",
                    StringComparison.Ordinal),
                $"{relativePath} uses prepare-before-publish reward ordering");

            var successfulMutationBoundary = source.LastIndexOf(
                "_registry.UpdateCharacter",
                prepare,
                StringComparison.Ordinal);
            if (successfulMutationBoundary < 0)
            {
                successfulMutationBoundary = source.LastIndexOf(
                    "var damageResult = hit.Result;",
                    prepare,
                    StringComparison.Ordinal);
            }
            if (successfulMutationBoundary < 0)
            {
                successfulMutationBoundary = source.LastIndexOf(
                    "out var damageResult",
                    prepare,
                    StringComparison.Ordinal);
            }

            Check.True(
                successfulMutationBoundary >= 0,
                $"{relativePath} exposes its successful mutation boundary");
            var beforePrepare =
                source[successfulMutationBoundary..prepare];
            Check.True(
                beforePrepare.Split(
                    "await ",
                    StringSplitOptions.None).Length == 2,
                $"{relativePath} has no cancellable await before reward preparation");
        }
    }

    private static void CheckAreaPreparationAdvancesProjection()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.Progression.cs"));
        var apply = source.IndexOf(
            "ApplyMonsterRewardProjection(settlement);",
            StringComparison.Ordinal);
        var pending = source.IndexOf(
            "return new PendingMonsterKillReward(",
            StringComparison.Ordinal);
        Check.True(
            apply >= 0 && pending > apply,
            "each prepared AOE reward advances the level used by the next kill");
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
            "Could not locate the Godswar repository root.");
    }
}
