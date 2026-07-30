using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresEquipmentForgeCommandIntegrationChecks
{
    private static async Task AssertFaultRecoveryAsync(
        string connectionString)
    {
        foreach (var stage in Enum.GetValues<
                     PostgresEquipmentForgeCommandStage>())
        {
            if (stage ==
                PostgresEquipmentForgeCommandStage.MaterialMutated)
            {
                continue;
            }

            await AssertFaultAtAsync(
                connectionString,
                stage,
                ordinal: -1);
        }

        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            await AssertFaultAtAsync(
                connectionString,
                PostgresEquipmentForgeCommandStage.MaterialMutated,
                ordinal);
        }
    }

    private static async Task AssertFaultAtAsync(
        string connectionString,
        PostgresEquipmentForgeCommandStage stage,
        int ordinal)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            $"f{(int)stage}{ordinal + 1}",
            odds:
            [
                (2, 4232, 1, 1),
                (3, 4232, 1, 1)
            ]);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var operationId = Guid.NewGuid();
        var throwing = CreateExecutor(
            source,
            () => 0,
            new ThrowingProbe(stage, ordinal));
        await ExpectInjectedFaultAsync(
            () => ExecuteAsync(
                throwing,
                fixture,
                operationId),
            stage,
            ordinal);

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        var committed =
            stage ==
                PostgresEquipmentForgeCommandStage.AfterCommit;
        if (committed)
        {
            Check.True(
                state.InventoryRevision == 1 &&
                state.WalletRevision == 1 &&
                state.InboxCount == 1 &&
                state.CurrencyLedgerCount == 1 &&
                state.InventoryLedgerCount == 4 &&
                state.OutboxCount == 1,
                $"after-commit fault at {stage}/{ordinal} preserves commit");
            var replay = await CreateExecutor(source, () => 99)
                .TryReplayAsync(
                    fixture.Subject,
                    PlayerOwnershipTestFences.ForCharacter(
                        fixture.Subject.CharacterId),
                    operationId);
            Check.Equal(
                (int)EquipmentForgeExecutionDisposition.Duplicate,
                (int)replay.Disposition,
                "provider uncertainty recovers by UUID replay");
            Check.Equal(
                0,
                replay.Receipt?.Roll ?? -1,
                "provider uncertainty returns the original sampled roll");
            return;
        }

        Check.True(
            state.Silver == 1_000 &&
            state.WalletRevision == 0 &&
            state.InventoryRevision == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.CurrencyLedgerCount == 0 &&
            state.InventoryLedgerCount == 0 &&
            state.OutboxCount == 0,
            $"fault at {stage}/{ordinal} rolls back every durable write");
        var retry = await ExecuteAsync(
            CreateExecutor(source, () => 0),
            fixture,
            operationId);
        Check.Equal(
            (int)EquipmentForgeExecutionDisposition.Committed,
            (int)retry.Disposition,
            $"fault at {stage}/{ordinal} permits exact safe retry");
    }

    private static async Task ExpectInjectedFaultAsync(
        Func<Task<EquipmentForgeExecutionResult>> action,
        PostgresEquipmentForgeCommandStage stage,
        int ordinal)
    {
        try
        {
            await action();
        }
        catch (InjectedForgeFault fault)
        {
            Check.True(
                fault.Stage == stage &&
                (ordinal < 0 || fault.Ordinal == ordinal),
                $"fault probe reached {stage}/{ordinal}");
            return;
        }

        throw new InvalidOperationException(
            $"Expected injected forge fault at {stage}/{ordinal}.");
    }
}
