using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresEquipmentForgeCommandIntegrationChecks
{
    private static async Task AssertReplayConflictAndRacesAsync(
        string connectionString)
    {
        var ownership = await CreateFixtureAsync(
            connectionString,
            "owner");
        await using var ownerSource =
            NpgsqlDataSource.Create(connectionString);
        var ownerRoll = new CountingRollSource(0);
        Check.True(
            EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                ownership.Equipment,
                ownership.Primary,
                ownership.Odds,
                out var ownerCommand),
            "ownership fixture creates a forge command");
        var wrongOwnerResult = await CreateExecutor(
                ownerSource,
                ownerRoll.Next)
            .ExecuteAsync(
                EquipmentForgeCommandEnvelope.Create(
                    new CommandSubject(
                        ownership.AccountId + 1,
                        ownership.CharacterId),
                    new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.SecureTlsLegacy),
                    DateTimeOffset.UtcNow,
                    ownerCommand));
        Check.Equal(
            (int)EquipmentForgeExecutionDisposition.PreconditionFailed,
            (int)wrongOwnerResult.Disposition,
            "forge binds character ownership to authenticated account");
        var ownershipState = await ReadStateAsync(
            connectionString,
            ownership);
        Check.True(
            ownerRoll.Calls == 0 &&
            ownershipState.AuditCount == 0 &&
            ownershipState.InboxCount == 0 &&
            ownershipState.InventoryLedgerCount == 0 &&
            ownershipState.OutboxCount == 0,
            "wrong-account forge creates no durable evidence or random roll");

        var replayFixture = await CreateFixtureAsync(
            connectionString,
            "replay");
        await using var replaySource =
            NpgsqlDataSource.Create(connectionString);
        var replayRoll = new CountingRollSource(0);
        var replayExecutor = CreateExecutor(
            replaySource,
            replayRoll.Next);
        var operationId = Guid.NewGuid();
        var original = RequireReceipt(
            await ExecuteAsync(
                replayExecutor,
                replayFixture,
                operationId),
            EquipmentForgeExecutionDisposition.Committed,
            "replay source forge");
        var duplicate = RequireReceipt(
            await replayExecutor.TryReplayAsync(
                replayFixture.Subject,
                operationId),
            EquipmentForgeExecutionDisposition.Duplicate,
            "exact UUID replay");
        AssertReceiptsEqual(
            original,
            duplicate,
            "exact UUID replay returns stored roll and receipt");
        Check.Equal(
            1,
            replayRoll.Calls,
            "replay never samples a second random roll");

        var changedExpected =
            (CompactItemEntry.Parse(
                replayFixture.Equipment.ExpectedCompactItemState) with
            {
                Quality = 2
            }).ToCompactString();
        var conflict = await ExecuteAsync(
            replayExecutor,
            replayFixture,
            operationId,
            equipment: replayFixture.Equipment with
            {
                ExpectedCompactItemState = changedExpected
            });
        Check.Equal(
            (int)EquipmentForgeExecutionDisposition.RequestHashConflict,
            (int)conflict.Disposition,
            "same UUID with different canonical request conflicts");
        var replayState = await ReadStateAsync(
            connectionString,
            replayFixture);
        Check.True(
            replayState.DuplicateCount == 1 &&
            replayState.ConflictCount == 1 &&
            replayState.InboxCount == 1,
            "replay and conflict counters advance without another command");

        var sameUuid = await CreateFixtureAsync(
            connectionString,
            "sameid");
        await using var sameSource =
            NpgsqlDataSource.Create(connectionString);
        var sameRoll = new CountingRollSource(0);
        var sameExecutor = CreateExecutor(sameSource, sameRoll.Next);
        var sharedId = Guid.NewGuid();
        var sameResults = await Task.WhenAll(
            ExecuteAsync(sameExecutor, sameUuid, sharedId),
            ExecuteAsync(sameExecutor, sameUuid, sharedId));
        Check.True(
            sameResults.Count(result =>
                result.Disposition ==
                    EquipmentForgeExecutionDisposition.Committed) == 1 &&
            sameResults.Count(result =>
                result.Disposition ==
                    EquipmentForgeExecutionDisposition.Duplicate) == 1 &&
            sameRoll.Calls == 1,
            "same UUID race commits and samples exactly once");

        var distinct = await CreateFixtureAsync(
            connectionString,
            "distinct");
        await using var distinctSource =
            NpgsqlDataSource.Create(connectionString);
        var distinctRoll = new CountingRollSource(0);
        var distinctExecutor =
            CreateExecutor(distinctSource, distinctRoll.Next);
        var distinctResults = await Task.WhenAll(
            ExecuteAsync(
                distinctExecutor,
                distinct,
                Guid.NewGuid()),
            ExecuteAsync(
                distinctExecutor,
                distinct,
                Guid.NewGuid()));
        Check.True(
            distinctResults.Count(result =>
                result.Disposition ==
                    EquipmentForgeExecutionDisposition.Committed) == 1 &&
            distinctResults.Count(result =>
                result.Disposition ==
                    EquipmentForgeExecutionDisposition.TerminalRejected) ==
                1 &&
            distinctResults.Single(result =>
                result.Disposition ==
                    EquipmentForgeExecutionDisposition.TerminalRejected)
                .Receipt?.Status ==
                    EquipmentForgeCommandResultStatus.StaleSelection &&
            distinctRoll.Calls == 1,
            "distinct UUID race serializes and durably rejects stale snapshot");
        var distinctState = await ReadStateAsync(
            connectionString,
            distinct);
        Check.True(
            distinctState.InboxCount == 2 &&
            distinctState.TerminalRejectedCount == 1 &&
            distinctState.InventoryRevision == 1,
            "distinct race stores one commit and one terminal decision");
    }
}
