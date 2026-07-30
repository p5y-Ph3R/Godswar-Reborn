using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static readonly PostgresMakeAttributeStoneCommandStage[]
        PreCommitStages =
        [
            PostgresMakeAttributeStoneCommandStage.AuditInserted,
            PostgresMakeAttributeStoneCommandStage.InboxInserted,
            PostgresMakeAttributeStoneCommandStage.InventoryMutated,
            PostgresMakeAttributeStoneCommandStage.LedgerInserted,
            PostgresMakeAttributeStoneCommandStage.OutboxInserted,
            PostgresMakeAttributeStoneCommandStage.BeforeCommit
        ];

    private static async Task AssertPreCommitFaultRollbackAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fault");
        var envelope = CreateEnvelope(fixture, Guid.NewGuid());

        foreach (var stage in PreCommitStages)
        {
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            await AssertInjectedFaultAsync(
                () => CreateExecutor(
                        source,
                        new ThrowingStoneProbe(stage))
                    .ExecuteAsync(envelope),
                stage);
            AssertInitialState(
                await ReadStateAsync(connectionString, fixture),
                RecipeDustQuantity,
                $"{stage} rolls back Dust, revision, inbox, audit, " +
                "ledger, and outbox");
        }

        await using var recoverySource =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await CreateExecutor(recoverySource)
                .ExecuteAsync(envelope),
            MakeAttributeStoneExecutionDisposition.Committed,
            "post-rollback Make Attribute Stone recovery");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "rolled-back operation remains available for one commit");
    }

    private static async Task AssertAfterCommitRecoveryAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ack");
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, operationId);

        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => CreateExecutor(
                        source,
                        new ThrowingStoneProbe(
                            PostgresMakeAttributeStoneCommandStage
                                .AfterCommit))
                    .ExecuteAsync(envelope),
                PostgresMakeAttributeStoneCommandStage.AfterCommit);
        }

        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "lost acknowledgement leaves the recipe committed");

        MakeAttributeStoneExecutionResult replay;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            replay = await CreateExecutor(source).TryReplayAsync(
                new CommandSubject(
                    fixture.AccountId,
                    fixture.CharacterId),
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.CharacterId),
                operationId);
        }

        var duplicate = RequireReceipt(
            replay,
            MakeAttributeStoneExecutionDisposition.Duplicate,
            "lost-ack Make Attribute Stone replay");
        Check.True(
            duplicate.CharacterId == fixture.CharacterId &&
            duplicate.Status ==
                MakeAttributeStoneResultStatus.Succeeded &&
            duplicate.SourceDustItemId == DustItemId &&
            duplicate.OutputStoneItemId == AttributeStoneItemId &&
            duplicate.InventoryRevision == 1 &&
            duplicate.OutboxEventId.HasValue &&
            duplicate.OutboxEventId.Value != Guid.Empty,
            "new executor recovers the committed receipt without " +
            "selection context");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "lost-ack replay does not duplicate inventory or evidence");
    }

    private static async Task AssertInjectedFaultAsync(
        Func<Task<MakeAttributeStoneExecutionResult>> action,
        PostgresMakeAttributeStoneCommandStage expectedStage)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedStoneCommandFault exception)
        {
            Check.True(
                exception.Stage == expectedStage,
                $"{expectedStage} fault reports its exact stage");
            return;
        }

        throw new InvalidOperationException(
            $"The {expectedStage} probe did not interrupt execution.");
    }

    private sealed class ThrowingStoneProbe(
        PostgresMakeAttributeStoneCommandStage stage) :
        IPostgresMakeAttributeStoneCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresMakeAttributeStoneCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedStoneCommandFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedStoneCommandFault(
        PostgresMakeAttributeStoneCommandStage stage) : Exception
    {
        public PostgresMakeAttributeStoneCommandStage Stage { get; } =
            stage;
    }
}
