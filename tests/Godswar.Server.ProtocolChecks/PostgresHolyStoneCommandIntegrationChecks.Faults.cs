using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        foreach (var stage in new[]
                 {
                     PostgresHolyStoneCommandStage.AuditInserted,
                     PostgresHolyStoneCommandStage.InboxInserted,
                     PostgresHolyStoneCommandStage.TargetMutated,
                     PostgresHolyStoneCommandStage.StoneMutated,
                     PostgresHolyStoneCommandStage
                         .InventoryRevisionAdvanced,
                     PostgresHolyStoneCommandStage
                         .InventoryLedgerInserted,
                     PostgresHolyStoneCommandStage.OutboxInserted,
                     PostgresHolyStoneCommandStage.BeforeCommit
                 })
        {
            await AssertMountRollbackAsync(
                connectionString,
                stage);
        }
        foreach (var stage in new[]
                 {
                     PostgresHolyStoneCommandStage.AuditInserted,
                     PostgresHolyStoneCommandStage.InboxInserted,
                     PostgresHolyStoneCommandStage.TargetMutated,
                     PostgresHolyStoneCommandStage.WalletUpdated,
                     PostgresHolyStoneCommandStage
                         .InventoryRevisionAdvanced,
                     PostgresHolyStoneCommandStage
                         .CurrencyLedgerInserted,
                     PostgresHolyStoneCommandStage
                         .InventoryLedgerInserted,
                     PostgresHolyStoneCommandStage.OutboxInserted,
                     PostgresHolyStoneCommandStage.BeforeCommit
                 })
        {
            await AssertDrillRollbackAsync(connectionString, stage);
        }
        await AssertRemoveOutputRollbackAsync(connectionString);
        await AssertAfterCommitReplayAsync(connectionString);
    }

    private static async Task AssertMountRollbackAsync(
        string connectionString,
        PostgresHolyStoneCommandStage stage)
    {
        var targetBefore = Weapon(1);
        var stoneBefore = SimpleItem(9060, grade: 6, stack: 2);
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"rb{(int)stage}",
            target: targetBefore,
            stone: stoneBefore);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(dataSource, new ThrowingProbe(stage)),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Mount),
            stage);
        Check.Equal(
            targetBefore,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                0,
                fixture.TargetSlot))!.Value.Item,
            $"{stage} rolls back target");
        Check.Equal(
            stoneBefore,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot))!.Value.Item,
            $"{stage} rolls back stone");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.Equal(0L, state.InventoryRevision, $"{stage} revision");
        Check.Equal(0L, state.WalletRevision, $"{stage} wallet revision");
        Check.Equal(1000, state.Gold, $"{stage} Gold rollback");
        Check.Equal(0L, state.AuditCount, $"{stage} audit rollback");
        Check.Equal(0L, state.InboxCount, $"{stage} inbox rollback");
        Check.Equal(
            0L,
            state.CurrencyLedgerCount,
            $"{stage} currency rollback");
        Check.Equal(0L, state.LedgerCount, $"{stage} ledger rollback");
        Check.Equal(0L, state.OutboxCount, $"{stage} outbox rollback");
        Check.True(
            state.WalletReconciled && state.InventoryReconciled,
            $"{stage} rollback reconciles");
    }

    private static async Task AssertDrillRollbackAsync(
        string connectionString,
        PostgresHolyStoneCommandStage stage)
    {
        var targetBefore = Weapon(0);
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"drb{(int)stage}",
            target: targetBefore);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(dataSource, new ThrowingProbe(stage)),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Drill),
            stage);
        Check.Equal(
            targetBefore,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                0,
                fixture.TargetSlot))!.Value.Item,
            $"{stage} rolls back drilled target");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Drill);
        Check.True(
            state.InventoryRevision == 0 &&
            state.WalletRevision == 0 &&
            state.Gold == 1000 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.CurrencyLedgerCount == 0 &&
            state.GoldLedgerDelta == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            $"{stage} rolls back the entire Drill transaction");
    }

    private static async Task AssertRemoveOutputRollbackAsync(
        string connectionString)
    {
        var targetBefore = Weapon(
            1,
            effect1: 2,
            level1: 8);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "rbout",
            target: targetBefore);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    new ThrowingProbe(
                        PostgresHolyStoneCommandStage.OutputInserted)),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandOperation.Remove,
                socketIndex: 0),
            PostgresHolyStoneCommandStage.OutputInserted);
        Check.Equal(
            targetBefore,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                0,
                fixture.TargetSlot))!.Value.Item,
            "output fault rolls back target");
        Check.True(
            await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                0) is null,
            "output fault rolls back inserted item");
    }

    private static async Task AssertAfterCommitReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "afterc",
            target: Weapon(1),
            stone: SimpleItem(9060, grade: 7, stack: 2));
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    new ThrowingProbe(
                        PostgresHolyStoneCommandStage.AfterCommit)),
                fixture,
                operationId,
                HolyStoneCommandOperation.Mount),
            PostgresHolyStoneCommandStage.AfterCommit);
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Mount);
        Check.Equal(1L, state.InventoryRevision, "after-commit persists");
        AssertCommittedEvidence(
            state,
            expectedLedger: 2,
            "after-commit");
        RequireReceipt(
            await CreateExecutor(dataSource).TryReplayAsync(
                fixture.Subject,
                HolyStoneCommandOperation.Mount,
                operationId),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Mounted,
            "after-commit replay");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<HolyStoneExecutionResult>> action,
        PostgresHolyStoneCommandStage expectedStage)
    {
        try
        {
            await action();
        }
        catch (InjectedHolyStoneFault exception)
        {
            Check.Equal(
                (int)expectedStage,
                (int)exception.Stage,
                "injected Holy Stone stage");
            return;
        }
        throw new InvalidOperationException(
            $"Expected injected Holy Stone fault at {expectedStage}.");
    }
}
