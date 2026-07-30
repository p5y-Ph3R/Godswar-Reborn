using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridActivationCommandIntegrationChecks
{
    private static async Task AssertFaultAtomicityAsync(
        string connectionString)
    {
        foreach (var stage in new[]
                 {
                     PostgresZodiacSkillGridActivationCommandStage
                         .AuditInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .InboxInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .GridMutated,
                     PostgresZodiacSkillGridActivationCommandStage
                         .WalletUpdated,
                     PostgresZodiacSkillGridActivationCommandStage
                         .CurrencyLedgerInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .OutboxInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .BeforeCommit
                 })
        {
            await AssertRollbackAtAsync(
                connectionString,
                stage,
                gridIndex: 1,
                gold: 5_000);
        }

        foreach (var stage in new[]
                 {
                     PostgresZodiacSkillGridActivationCommandStage
                         .AuditInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .InboxInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .GridMutated,
                     PostgresZodiacSkillGridActivationCommandStage
                         .OutboxInserted,
                     PostgresZodiacSkillGridActivationCommandStage
                         .BeforeCommit
                 })
        {
            await AssertRollbackAtAsync(
                connectionString,
                stage,
                gridIndex: 0,
                gold: 5_000);
        }

        await AssertAfterCommitReplayAsync(connectionString);
    }

    private static async Task AssertRollbackAtAsync(
        string connectionString,
        PostgresZodiacSkillGridActivationCommandStage stage,
        int gridIndex,
        int gold)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"rb{gridIndex}{(int)stage}",
            gold);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => CreateExecutor(
                dataSource,
                new ThrowingProbe(stage)).ExecuteAsync(
                    CreateEnvelope(fixture, gridIndex)),
            stage);
        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex),
            gold,
            $"{stage} grid-{gridIndex} rollback");
    }

    private static async Task AssertAfterCommitReplayAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "afterc",
            gold: 5_000);
        var envelope = CreateEnvelope(fixture, gridIndex);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => CreateExecutor(
                dataSource,
                new ThrowingProbe(
                    PostgresZodiacSkillGridActivationCommandStage
                        .AfterCommit)).ExecuteAsync(envelope),
            PostgresZodiacSkillGridActivationCommandStage.AfterCommit);

        AssertCommitted(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex),
            expectedGold: 2_700,
            expectedWalletRevision: 1,
            expectedLedgerCount: 1,
            expectedLedgerDelta: -2_300,
            "after-commit uncertainty");
        var duplicate = RequireReceipt(
            await CreateExecutor(dataSource).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    gridIndex,
                    connectionId: Guid.NewGuid())),
            ZodiacSkillGridActivationExecutionDisposition.Duplicate,
            "after-commit activation replay");
        Check.True(
            duplicate.GoldAfter == 2_700 &&
            duplicate.WalletRevision == 1,
            "after-commit replay recovers the committed wallet receipt");
        var replayed = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex);
        Check.True(
            replayed.CurrencyLedgerCount == 1 &&
            replayed.OutboxCount == 1 &&
            replayed.DuplicateCount == 1 &&
            replayed.WalletReconciled,
            "after-commit replay neither redebits nor republishes");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<ZodiacSkillGridActivationExecutionResult>> action,
        PostgresZodiacSkillGridActivationCommandStage expectedStage)
    {
        try
        {
            await action();
        }
        catch (InjectedZodiacActivationFault exception)
        {
            Check.Equal(
                (int)expectedStage,
                (int)exception.Stage,
                "injected Zodiac activation stage");
            return;
        }

        throw new InvalidOperationException(
            $"Expected injected Zodiac fault at {expectedStage}.");
    }
}
