using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridUpgradeCommandIntegrationChecks
{
    private static async Task AssertConcurrencyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race",
            energy: 20,
            energyRemainderX100: 77,
            talentPoints: 30);
        var operationId = Guid.NewGuid();
        var first = CreateEnvelope(fixture, 0, operationId);
        var second = CreateEnvelope(
            fixture,
            0,
            operationId,
            connectionId: Guid.NewGuid());
        await using var firstSource =
            NpgsqlDataSource.Create(connectionString);
        await using var secondSource =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            CreateExecutor(firstSource).ExecuteAsync(first),
            CreateExecutor(secondSource).ExecuteAsync(second));
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition.Committed),
            "one concurrent Zodiac upgrade commits");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition.Duplicate),
            "one concurrent Zodiac upgrade replays");
        var committed = results.Single(result =>
            result.Disposition ==
            ZodiacSkillGridUpgradeExecutionDisposition.Committed)
            .Receipt ??
            throw new InvalidOperationException(
                "The concurrent Zodiac winner returned no receipt.");
        var duplicate = results.Single(result =>
            result.Disposition ==
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate)
            .Receipt ??
            throw new InvalidOperationException(
                "The concurrent Zodiac replay returned no receipt.");
        Check.Equal(
            committed,
            duplicate,
            "concurrent upgrades return one canonical receipt");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            state.Energy == 15 &&
            state.EnergyRemainderX100 == 77 &&
            state.TalentPoints == 23 &&
            state.Level == 2 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.CurrencyLedgerCount == 0,
            "concurrent exact UUID spends and publishes once");
    }

    private static async Task AssertFaultAtomicityAsync(
        string connectionString)
    {
        foreach (var stage in new[]
                 {
                     PostgresZodiacSkillGridUpgradeCommandStage
                         .AuditInserted,
                     PostgresZodiacSkillGridUpgradeCommandStage
                         .InboxInserted,
                     PostgresZodiacSkillGridUpgradeCommandStage
                         .ResourcesUpdated,
                     PostgresZodiacSkillGridUpgradeCommandStage
                         .GridUpdated,
                     PostgresZodiacSkillGridUpgradeCommandStage
                         .OutboxInserted,
                     PostgresZodiacSkillGridUpgradeCommandStage
                         .BeforeCommit
                 })
        {
            await AssertSuccessRollbackAtAsync(
                connectionString,
                stage);
        }

        await AssertTerminalRollbackAsync(connectionString);
        await AssertAfterCommitReplayAsync(connectionString);
    }

    private static async Task AssertSuccessRollbackAtAsync(
        string connectionString,
        PostgresZodiacSkillGridUpgradeCommandStage stage)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"rb{(int)stage}",
            energy: 20,
            energyRemainderX100: 63,
            talentPoints: 30);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => CreateExecutor(
                dataSource,
                new ThrowingProbe(stage)).ExecuteAsync(
                    CreateEnvelope(fixture, 0, Guid.NewGuid())),
            stage);
        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex: 0),
            expectedEnergy: 20,
            expectedRemainder: 63,
            expectedTalentPoints: 30,
            expectedLevel: 1,
            $"{stage} rollback");
    }

    private static async Task AssertTerminalRollbackAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "termrb",
            energy: 0,
            energyRemainderX100: 0,
            talentPoints: 30);
        var stage =
            PostgresZodiacSkillGridUpgradeCommandStage.BeforeCommit;
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => CreateExecutor(
                dataSource,
                new ThrowingProbe(stage)).ExecuteAsync(
                    CreateEnvelope(fixture, 0, Guid.NewGuid())),
            stage);
        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex: 0),
            expectedEnergy: 0,
            expectedRemainder: 0,
            expectedTalentPoints: 30,
            expectedLevel: 1,
            "terminal rejection before-commit rollback");
    }

    private static async Task AssertAfterCommitReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "afterc",
            energy: 20,
            energyRemainderX100: 41,
            talentPoints: 30);
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, 0, operationId);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => CreateExecutor(
                dataSource,
                new ThrowingProbe(
                    PostgresZodiacSkillGridUpgradeCommandStage
                        .AfterCommit)).ExecuteAsync(envelope),
            PostgresZodiacSkillGridUpgradeCommandStage.AfterCommit);
        AssertCommitted(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex: 0),
            expectedEnergy: 15,
            expectedRemainder: 41,
            expectedTalentPoints: 23,
            expectedLevel: 2,
            "after-commit uncertainty");

        var duplicate = RequireReceipt(
            await CreateExecutor(dataSource).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    0,
                    operationId,
                    connectionId: Guid.NewGuid())),
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate,
            "after-commit Zodiac replay");
        Check.True(
            duplicate.EnergyAfter == 15 &&
            duplicate.EnergyRemainderAfterX100 == 41 &&
            duplicate.TalentPointsAfter == 23 &&
            duplicate.CurrentLevel == 2,
            "after-commit replay recovers exact resource evidence");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            state.OutboxCount == 1 &&
            state.InboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.CurrencyLedgerCount == 0,
            "after-commit replay neither respends nor republishes");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<ZodiacSkillGridUpgradeExecutionResult>> action,
        PostgresZodiacSkillGridUpgradeCommandStage expectedStage)
    {
        try
        {
            await action();
        }
        catch (InjectedZodiacUpgradeFault exception)
        {
            Check.Equal(
                (int)expectedStage,
                (int)exception.Stage,
                "injected Zodiac upgrade stage");
            return;
        }

        throw new InvalidOperationException(
            $"Expected injected Zodiac fault at {expectedStage}.");
    }
}
