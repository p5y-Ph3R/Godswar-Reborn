using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearEnhancementIntegrationChecks
{
    private static readonly PostgresGearEnhancementCommandStage[]
        PreCommitStages =
        [
            PostgresGearEnhancementCommandStage.AuditInserted,
            PostgresGearEnhancementCommandStage.InboxInserted,
            PostgresGearEnhancementCommandStage.GearMutated,
            PostgresGearEnhancementCommandStage.CatalystMutated,
            PostgresGearEnhancementCommandStage.AttributeStoneMutated,
            PostgresGearEnhancementCommandStage
                .InventoryRevisionAdvanced,
            PostgresGearEnhancementCommandStage.LedgerInserted,
            PostgresGearEnhancementCommandStage.OutboxInserted,
            PostgresGearEnhancementCommandStage.BeforeCommit
        ];

    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        await AssertPreCommitFaultRollbackAsync(connectionString);
        await AssertAfterCommitRecoveryAsync(connectionString);
    }

    private static async Task AssertPreCommitFaultRollbackAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fault",
            GearEnhancementCommandOperation.Enhance);
        var operationId = Guid.NewGuid();
        foreach (var stage in PreCommitStages)
        {
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            await AssertInjectedFaultAsync(
                () => ExecuteAsync(
                    CreateExecutor(source, new ThrowingProbe(stage)),
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
                $"{stage} rolls back every Gear Enhancement write");
            Check.True(
                (await ReadItemAsync(
                    connectionString,
                    fixture.CharacterId,
                    checked((short)fixture.Gear.KitBagSlot))) is
                    { Attribute1: 0, AttributeLevel1: 1 } &&
                (await ReadItemAsync(
                    connectionString,
                    fixture.CharacterId,
                    checked((short)fixture.Catalyst.KitBagSlot)))?.Stack ==
                    2 &&
                (await ReadItemAsync(
                    connectionString,
                    fixture.CharacterId,
                    checked((short)fixture.Stone.KitBagSlot)))?.Stack == 2,
                $"{stage} restores every item exactly");
        }

        await using var recoverySource =
            NpgsqlDataSource.Create(connectionString);
        var recovered = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(recoverySource),
                fixture,
                operationId),
            GearEnhancementExecutionDisposition.Committed,
            "post-rollback Gear Enhancement recovery");
        Check.True(
            recovered.InventoryRevision == 1 &&
            recovered.Mutations.Length == 3,
            "rolled-back UUID remains available for one exact commit");
    }

    private static async Task AssertAfterCommitRecoveryAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ack",
            GearEnhancementCommandOperation.Delete,
            npcId: GearEnhancementCommandEnvelope
                .AthensOriginEnhancerNpcId,
            dialogIndex: GearEnhancementCommandEnvelope
                .OriginEnhancerDialogIndex);
        var operationId = Guid.NewGuid();
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => ExecuteAsync(
                    CreateExecutor(
                        source,
                        new ThrowingProbe(
                            PostgresGearEnhancementCommandStage
                                .AfterCommit)),
                    fixture,
                    operationId),
                PostgresGearEnhancementCommandStage.AfterCommit);
        }

        var committedState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            committedState.InventoryRevision == 1 &&
            committedState.AuditCount == 1 &&
            committedState.InboxCount == 1 &&
            committedState.LedgerCount == 3 &&
            committedState.OutboxCount == 1 &&
            committedState.DuplicateCount == 0,
            "after-commit fault leaves exactly one durable mutation");

        await using var replaySource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(replaySource);
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                fixture.Operation,
                operationId),
            GearEnhancementExecutionDisposition.Duplicate,
            "lost-response explicit replay");
        Check.True(
            replay.NpcId ==
                GearEnhancementCommandEnvelope
                    .AthensOriginEnhancerNpcId &&
            replay.DialogIndex ==
                GearEnhancementCommandEnvelope
                    .OriginEnhancerDialogIndex &&
            replay.Mutations.Length == 3,
            "lost-response replay restores exact endpoint and evidence");
        var duplicate = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            GearEnhancementExecutionDisposition.Duplicate,
            "lost-response command retry");
        AssertReceiptsEqual(
            replay,
            duplicate,
            "command retry returns exact stored committed outcome");
        var finalState = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            finalState.InventoryRevision == 1 &&
            finalState.LedgerCount == 3 &&
            finalState.OutboxCount == 1 &&
            finalState.DuplicateCount == 2,
            "both replay paths only increment duplicate count");
    }

    private static async Task AssertInjectedFaultAsync(
        Func<Task<GearEnhancementExecutionResult>> action,
        PostgresGearEnhancementCommandStage expectedStage)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedEnhancementFault exception)
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
