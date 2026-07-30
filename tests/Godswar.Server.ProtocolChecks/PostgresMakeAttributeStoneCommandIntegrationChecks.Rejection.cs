using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Commands;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task AssertTerminalRejectionReplayAsync(
        string connectionString)
    {
        const short insufficientStack = RecipeDustQuantity - 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "reject",
            insufficientStack);
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, operationId);

        MakeAttributeStoneExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(source)
                .ExecuteAsync(envelope);
        }

        var rejected = RequireReceipt(
            first,
            MakeAttributeStoneExecutionDisposition.TerminalRejected,
            "insufficient-Dust Make Attribute Stone command");
        Check.True(
            rejected.Status ==
                MakeAttributeStoneResultStatus.InsufficientDust &&
            rejected.NativeResultSubId ==
                MakeAttributeStoneNativeResults
                    .InsufficientDustSubId &&
            rejected.SourceDustItemId == DustItemId &&
            rejected.OutputStoneItemId == AttributeStoneItemId &&
            rejected.IsBound == fixture.IsBound &&
            rejected.InventoryRevision == 0 &&
            rejected.OutboxEventId is null,
            "terminal rejection records the canonical native result");
        AssertRejectedState(
            await ReadStateAsync(connectionString, fixture),
            insufficientStack,
            expectedDuplicateCount: 0,
            "terminal rejection persists identity but no mutation");

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
            "selection-free terminal-rejection replay");
        AssertReceiptsEqual(
            rejected,
            duplicate,
            "terminal rejection replays without ephemeral selection");
        Check.True(
            !replay.IsSuccess,
            "a replayed terminal rejection remains unsuccessful");
        AssertRejectedState(
            await ReadStateAsync(connectionString, fixture),
            insufficientStack,
            expectedDuplicateCount: 1,
            "terminal rejection retry never consumes Dust later");
    }

    private static void AssertRejectedState(
        StoneDurableState state,
        short expectedDustQuantity,
        int expectedDuplicateCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.DustQuantity == expectedDustQuantity &&
            state.StoneQuantity == 0 &&
            state.StoneItemCount == 0 &&
            state.StoneBound == -1 &&
            state.StoneSlot == -1 &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.RecipeLedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 0 &&
            state.RejectedInboxCount == 1 &&
            state.IsReconciled,
            description);
    }
}
