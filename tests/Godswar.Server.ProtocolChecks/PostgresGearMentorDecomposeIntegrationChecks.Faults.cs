using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorDecomposeIntegrationChecks
{
    private static readonly PostgresGearMentorDecomposeCommandStage[]
        PreCommitStages =
        [
            PostgresGearMentorDecomposeCommandStage.AuditInserted,
            PostgresGearMentorDecomposeCommandStage.InboxInserted,
            PostgresGearMentorDecomposeCommandStage.InventoryMutated,
            PostgresGearMentorDecomposeCommandStage.LedgerInserted,
            PostgresGearMentorDecomposeCommandStage.OutboxInserted,
            PostgresGearMentorDecomposeCommandStage.BeforeCommit
        ];

    private static async Task AssertConcurrentDuplicateAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race",
            [new GearSpec(4, 1004)]);
        var operationId = Guid.NewGuid();
        var random = new CountingRandomSource();
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source, random);
        var results = await Task.WhenAll(
            ExecuteAsync(executor, fixture, operationId),
            ExecuteAsync(executor, fixture, operationId));
        Check.True(
            results.Count(static result =>
                result.Disposition ==
                    GearMentorDecomposeGearExecutionDisposition
                        .Committed) == 1 &&
            results.Count(static result =>
                result.Disposition ==
                    GearMentorDecomposeGearExecutionDisposition
                        .Duplicate) == 1,
            "concurrent identical UUID yields one commit and one replay");
        var firstReceipt = results[0].Receipt ??
            throw new InvalidOperationException(
                "Concurrent result has no receipt.");
        var secondReceipt = results[1].Receipt ??
            throw new InvalidOperationException(
                "Concurrent result has no receipt.");
        AssertReceiptsEqual(
            firstReceipt,
            secondReceipt,
            "concurrent duplicate returns identical random Dust");
        Check.Equal(
            1,
            random.CallCount,
            "concurrent duplicate invokes random source only once");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1,
            "concurrent duplicate creates one atomic mutation");
    }

    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        await AssertPreCommitFaultRollbackAsync(connectionString);
        await AssertAfterCommitRecoveryAsync(connectionString);
    }

    private static async Task AssertConcurrentDistinctOperationsAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race2",
            [new GearSpec(4, 1004)]);
        var random = new CountingRandomSource();
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source, random);
        var results = await Task.WhenAll(
            ExecuteAsync(executor, fixture, Guid.NewGuid()),
            ExecuteAsync(executor, fixture, Guid.NewGuid()));
        Check.True(
            results.Count(static result =>
                result.Disposition ==
                    GearMentorDecomposeGearExecutionDisposition
                        .Committed) == 1 &&
            results.Count(static result =>
                result.Disposition ==
                    GearMentorDecomposeGearExecutionDisposition
                        .TerminalRejected) == 1,
            "distinct UUID race yields one commit and one durable reject");
        var rejected = results.Single(static result =>
            result.Disposition ==
                GearMentorDecomposeGearExecutionDisposition
                    .TerminalRejected);
        Check.True(
            rejected.Receipt?.Status ==
                GearMentorDecomposeGearResultStatus.SelectionMissing &&
            rejected.Receipt.InventoryRevision == 1,
            "losing distinct UUID observes authoritative missing gear");
        Check.Equal(
            1,
            random.CallCount,
            "distinct UUID race selects random Dust for winner only");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 2 &&
            state.InboxCount == 2 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 1 &&
            state.RejectedInboxCount == 1,
            "distinct UUID loser records no second player-value mutation");
    }

    private static async Task AssertPreCommitFaultRollbackAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fault",
            [new GearSpec(4, 1004)]);
        var operationId = Guid.NewGuid();
        var random = new CountingRandomSource();
        foreach (var stage in PreCommitStages)
        {
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            await AssertInjectedFaultAsync(
                () => ExecuteAsync(
                    CreateExecutor(
                        source,
                        random,
                        new ThrowingProbe(stage)),
                    fixture,
                    operationId),
                stage);
            var state = await ReadStateAsync(
                connectionString,
                fixture);
            Check.True(
                state.InventoryRevision == 0 &&
                state.AuditCount == 0 &&
                state.InboxCount == 0 &&
                state.LedgerCount == 0 &&
                state.OutboxCount == 0,
                $"{stage} rolls back every Decompose write");
        }

        Check.Equal(
            PreCommitStages.Length,
            random.CallCount,
            "each fully rolled-back first attempt may select anew");
        await using var recoverySource =
            NpgsqlDataSource.Create(connectionString);
        var recovered = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(recoverySource, random),
                fixture,
                operationId),
            GearMentorDecomposeGearExecutionDisposition.Committed,
            "post-rollback Decompose recovery");
        Check.True(
            recovered.InventoryRevision == 1 &&
            recovered.DustOutcomes.Length == 1,
            "rolled-back UUID remains available for one commit");
        Check.Equal(
            PreCommitStages.Length + 1,
            random.CallCount,
            "recovery selects Dust exactly once");
    }

    private static async Task AssertAfterCommitRecoveryAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ack",
            [new GearSpec(4, 1004, Attribute1: null)]);
        var operationId = Guid.NewGuid();
        var random = new CountingRandomSource(
            static (upperBound, _) => upperBound - 1);
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => ExecuteAsync(
                    CreateExecutor(
                        source,
                        random,
                        new ThrowingProbe(
                            PostgresGearMentorDecomposeCommandStage
                                .AfterCommit)),
                    fixture,
                    operationId),
                PostgresGearMentorDecomposeCommandStage.AfterCommit);
        }

        Check.Equal(
            1,
            random.CallCount,
            "lost response follows one committed random selection");
        var committedState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            committedState.InventoryRevision == 1 &&
            committedState.AuditCount == 1 &&
            committedState.InboxCount == 1 &&
            committedState.LedgerCount == 2 &&
            committedState.OutboxCount == 1 &&
            committedState.DuplicateCount == 0,
            "after-commit fault leaves exactly one durable mutation");

        await using var replaySource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(replaySource, random);
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                operationId),
            GearMentorDecomposeGearExecutionDisposition.Duplicate,
            "lost-response explicit replay");
        Check.True(
            replay.DustOutcomes.Single().DustItemId == 9921 &&
            replay.DustOutcomes.Single().Quantity == 2,
            "lost-response replay restores the exact committed Dust");
        Check.Equal(
            1,
            random.CallCount,
            "lost-response replay never rerolls Dust");

        var duplicate = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            GearMentorDecomposeGearExecutionDisposition.Duplicate,
            "lost-response command retry");
        AssertReceiptsEqual(
            replay,
            duplicate,
            "command retry returns the stored random outcome");
        Check.Equal(
            1,
            random.CallCount,
            "lost-response command retry never rerolls Dust");
        var finalState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            finalState.InventoryRevision == 1 &&
            finalState.LedgerCount == 2 &&
            finalState.OutboxCount == 1 &&
            finalState.DuplicateCount == 2,
            "both replay paths only increment bounded duplicate count");
    }

    private static async Task AssertInjectedFaultAsync(
        Func<Task<GearMentorDecomposeGearExecutionResult>> action,
        PostgresGearMentorDecomposeCommandStage expectedStage)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedDecomposeFault exception)
        {
            Check.Equal(
                (int)expectedStage,
                (int)exception.Stage,
                $"{expectedStage} fault reports its exact stage");
            return;
        }

        throw new InvalidOperationException(
            $"The {expectedStage} probe did not interrupt execution.");
    }
}
