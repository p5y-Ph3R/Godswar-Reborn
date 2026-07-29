using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemDeleteCommandIntegrationChecks
{
    private static async Task AssertReplayConflictAndOwnershipAsync(
        string connectionString)
    {
        await AssertConcurrentReplayAndConflictAsync(connectionString);
        await AssertWrongAccountRejectedAsync(connectionString);
    }

    private static async Task AssertConcurrentReplayAndConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race");
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var results = await Task.WhenAll(
            ExecuteAsync(executor, fixture, operationId),
            ExecuteAsync(executor, fixture, operationId));
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                KitBagItemDeleteExecutionDisposition.Committed),
            "concurrent exact delete has one committer");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                KitBagItemDeleteExecutionDisposition.Duplicate),
            "concurrent exact delete has one duplicate");
        AssertReceiptsEqual(
            results[0].Receipt ??
                throw new InvalidDataException(
                    "Concurrent delete returned no first receipt."),
            results[1].Receipt ??
                throw new InvalidDataException(
                    "Concurrent delete returned no second receipt."),
            "concurrent delete returns one exact durable outcome");

        var conflict = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            expectedState: "[]");
        Check.Equal(
            (int)KitBagItemDeleteExecutionDisposition
                .RequestHashConflict,
            (int)conflict.Disposition,
            "same operation UUID with different intent conflicts");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.TargetItemCount == 0 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.CompatibilityAuditCount == 1 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.ConflictCount == 1 &&
            state.IsReconciled,
            "concurrent replay and conflict cannot duplicate deletion");
    }

    private static async Task AssertWrongAccountRejectedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner");
        var other = await CreateFixtureAsync(
            connectionString,
            "other");
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var wrongSubject = new CommandSubject(
            other.AccountId,
            fixture.CharacterId);

        var result = await ExecuteAsync(
            executor,
            fixture,
            Guid.NewGuid(),
            subject: wrongSubject);
        Check.Equal(
            (int)KitBagItemDeleteExecutionDisposition
                .PreconditionFailed,
            (int)result.Disposition,
            "account cannot delete another account's character item");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.TargetItemCount == 1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.CompatibilityAuditCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.IsReconciled,
            "wrong-account delete leaves no durable evidence or mutation");
    }
}
