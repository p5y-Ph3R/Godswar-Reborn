using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static readonly
        PostgresGearMentorMaterialConversionCommandStage[]
        PreCommitStages =
        [
            PostgresGearMentorMaterialConversionCommandStage
                .AuditInserted,
            PostgresGearMentorMaterialConversionCommandStage
                .InboxInserted,
            PostgresGearMentorMaterialConversionCommandStage
                .InventoryMutated,
            PostgresGearMentorMaterialConversionCommandStage
                .LedgerInserted,
            PostgresGearMentorMaterialConversionCommandStage
                .OutboxInserted,
            PostgresGearMentorMaterialConversionCommandStage.BeforeCommit
        ];

    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        foreach (var family in new[]
                 {
                     CommandFamily.GearMentorTransformCrystal,
                     CommandFamily.GearMentorCombineGemPieces
                 })
        {
            await AssertPreCommitFaultRollbackAsync(
                connectionString,
                family);
            await AssertAfterCommitRecoveryAsync(
                connectionString,
                family);
        }
    }

    private static async Task AssertPreCommitFaultRollbackAsync(
        string connectionString,
        CommandFamily family)
    {
        var fixture = await CreateDefaultFixtureAsync(
            connectionString,
            "fault",
            family);
        var operationId = Guid.NewGuid();
        foreach (var stage in PreCommitStages)
        {
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            await AssertInjectedFaultAsync(
                () => ExecuteAsync(
                    CreateExecutor(
                        source,
                        new ThrowingConversionProbe(stage)),
                    fixture,
                    operationId),
                stage);
            AssertInitialState(
                await ReadStateAsync(connectionString, fixture),
                fixture,
                $"{family} {stage} rolls back all state");
        }

        await using var recoverySource =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(recoverySource),
                fixture,
                operationId),
            GearMentorMaterialConversionExecutionDisposition.Committed,
            $"{family} post-rollback recovery");
        var recovered = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            recovered.InventoryRevision == 1 &&
            recovered.SourceQuantity == 0 &&
            recovered.OutputQuantity == fixture.OutputQuantity &&
            recovered.AuditCount == 1 &&
            recovered.InboxCount == 1 &&
            recovered.LedgerCount == 1 &&
            recovered.OutboxCount == 1 &&
            recovered.IsReconciled,
            $"{family} rolled-back UUID remains available once");
    }

    private static async Task AssertAfterCommitRecoveryAsync(
        string connectionString,
        CommandFamily family)
    {
        var fixture = await CreateDefaultFixtureAsync(
            connectionString,
            "ack",
            family);
        var operationId = Guid.NewGuid();
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => ExecuteAsync(
                    CreateExecutor(
                        source,
                        new ThrowingConversionProbe(
                            PostgresGearMentorMaterialConversionCommandStage
                                .AfterCommit)),
                    fixture,
                    operationId),
                PostgresGearMentorMaterialConversionCommandStage
                    .AfterCommit);
        }

        var committedState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            committedState.InventoryRevision == 1 &&
            committedState.SourceQuantity == 0 &&
            committedState.OutputQuantity == fixture.OutputQuantity &&
            committedState.AuditCount == 1 &&
            committedState.InboxCount == 1 &&
            committedState.LedgerCount == 1 &&
            committedState.OutboxCount == 1 &&
            committedState.DuplicateCount == 0 &&
            committedState.IsReconciled,
            $"{family} lost acknowledgement leaves one commit");

        GearMentorMaterialConversionExecutionResult replay;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            replay = await ReplayAsync(
                CreateExecutor(source),
                fixture,
                operationId);
        }
        var receipt = RequireReceipt(
            replay,
            GearMentorMaterialConversionExecutionDisposition.Duplicate,
            $"{family} lost-ack replay");
        Check.True(
            receipt.Family == family &&
            receipt.Status ==
                GearMentorMaterialConversionResultStatus.Succeeded &&
            receipt.InventoryRevision == 1 &&
            receipt.OutboxEventId.HasValue,
            $"{family} lost-ack replay recovers stored receipt");
        var replayedState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            replayedState.DuplicateCount == 1 &&
            replayedState.InventoryRevision == 1 &&
            replayedState.LedgerCount == 1 &&
            replayedState.OutboxCount == 1,
            $"{family} lost-ack replay does not duplicate value");
    }

    private static void AssertInitialState(
        ConversionDurableState state,
        ConversionFixture fixture,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.SourceQuantity == fixture.InitialSourceStack &&
            state.OutputQuantity == 0 &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 0 &&
            state.RejectedInboxCount == 0 &&
            state.IsReconciled,
            description);
    }

    private static async Task AssertInjectedFaultAsync(
        Func<Task<GearMentorMaterialConversionExecutionResult>> action,
        PostgresGearMentorMaterialConversionCommandStage expectedStage)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedConversionCommandFault exception)
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

    private sealed class ThrowingConversionProbe(
        PostgresGearMentorMaterialConversionCommandStage stage) :
        IPostgresGearMentorMaterialConversionCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresGearMentorMaterialConversionCommandStage
                reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedConversionCommandFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedConversionCommandFault(
        PostgresGearMentorMaterialConversionCommandStage stage) :
        Exception
    {
        public PostgresGearMentorMaterialConversionCommandStage Stage
        {
            get;
        } = stage;
    }
}
