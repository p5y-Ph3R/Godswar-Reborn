using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static async Task AssertTerminalRejectionsAsync(
        string connectionString)
    {
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "tinv",
                CommandFamily.GearMentorTransformCrystal,
                sourceItemId: 4230,
                sourceStack: 1,
                outputItemId: 4233,
                outputQuantity: 2),
            GearMentorMaterialConversionResultStatus.InvalidCrystal,
            expectedSourceItemId: 4230,
            expectedOutputItemId: 0,
            expectedOutputQuantity: 0,
            expectedBagCount: 1);
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "cinv",
                CommandFamily.GearMentorCombineGemPieces,
                sourceItemId: 4230,
                sourceStack: 99,
                outputItemId: 4215,
                outputQuantity: 1),
            GearMentorMaterialConversionResultStatus.InvalidGemPieces,
            expectedSourceItemId: 4230,
            expectedOutputItemId: 0,
            expectedOutputQuantity: 0,
            expectedBagCount: 1);
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "cshort",
                CommandFamily.GearMentorCombineGemPieces,
                sourceItemId: 4216,
                sourceStack: 98,
                outputItemId: 4215,
                outputQuantity: 1),
            GearMentorMaterialConversionResultStatus
                .InsufficientGemPieces,
            expectedSourceItemId: 4216,
            expectedOutputItemId: 4215,
            expectedOutputQuantity: 1,
            expectedBagCount: 1);
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "tfull",
                CommandFamily.GearMentorTransformCrystal,
                sourceItemId: 4234,
                sourceStack: 2,
                outputItemId: 4233,
                outputQuantity: 2,
                fillRemainingBag: true),
            GearMentorMaterialConversionResultStatus
                .InsufficientCapacity,
            expectedSourceItemId: 4234,
            expectedOutputItemId: 4233,
            expectedOutputQuantity: 2,
            expectedBagCount: 96);
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "cfull",
                CommandFamily.GearMentorCombineGemPieces,
                sourceItemId: 4216,
                sourceStack: 100,
                outputItemId: 4215,
                outputQuantity: 1,
                fillRemainingBag: true),
            GearMentorMaterialConversionResultStatus
                .InsufficientCapacity,
            expectedSourceItemId: 4216,
            expectedOutputItemId: 4215,
            expectedOutputQuantity: 1,
            expectedBagCount: 96);
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "tmiss",
                CommandFamily.GearMentorTransformCrystal,
                sourceItemId: 4234,
                sourceStack: 1,
                outputItemId: 4233,
                outputQuantity: 2,
                includeSource: false),
            GearMentorMaterialConversionResultStatus.StaleSelection,
            expectedSourceItemId: 0,
            expectedOutputItemId: 0,
            expectedOutputQuantity: 0,
            expectedBagCount: 0);
        await AssertTerminalRejectionAsync(
            connectionString,
            await CreateFixtureAsync(
                connectionString,
                "cmiss",
                CommandFamily.GearMentorCombineGemPieces,
                sourceItemId: 4216,
                sourceStack: 99,
                outputItemId: 4215,
                outputQuantity: 1,
                includeSource: false),
            GearMentorMaterialConversionResultStatus.StaleSelection,
            expectedSourceItemId: 0,
            expectedOutputItemId: 0,
            expectedOutputQuantity: 0,
            expectedBagCount: 0);
    }

    private static async Task AssertTerminalRejectionAsync(
        string connectionString,
        ConversionFixture fixture,
        GearMentorMaterialConversionResultStatus expectedStatus,
        uint expectedSourceItemId,
        uint expectedOutputItemId,
        int expectedOutputQuantity,
        long expectedBagCount)
    {
        var operationId = Guid.NewGuid();
        GearMentorMaterialConversionExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                operationId);
        }

        var rejected = RequireReceipt(
            first,
            GearMentorMaterialConversionExecutionDisposition
                .TerminalRejected,
            $"{fixture.Family} {expectedStatus}");
        var identityKnown =
            expectedStatus !=
            GearMentorMaterialConversionResultStatus.StaleSelection;
        Check.True(
            rejected.Family == fixture.Family &&
            rejected.Status == expectedStatus &&
            rejected.NativeResultSubId ==
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    fixture.Family,
                    expectedStatus) &&
            rejected.SourceItemId == expectedSourceItemId &&
            rejected.OutputItemId == expectedOutputItemId &&
            rejected.OutputQuantity == expectedOutputQuantity &&
            rejected.IsBound ==
                (identityKnown ? fixture.IsBound : null) &&
            rejected.InventoryRevision == 0 &&
            rejected.OutboxEventId is null,
            $"{fixture.Family} {expectedStatus} receipt is canonical");

        GearMentorMaterialConversionExecutionResult replay;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            replay = await ReplayAsync(
                CreateExecutor(source),
                fixture,
                operationId);
        }
        AssertReceiptsEqual(
            rejected,
            RequireReceipt(
                replay,
                GearMentorMaterialConversionExecutionDisposition
                    .Duplicate,
                $"{fixture.Family} {expectedStatus} replay"),
            $"{fixture.Family} terminal rejection replays exactly");
        Check.True(
            !replay.IsSuccess,
            $"{fixture.Family} replayed rejection stays unsuccessful");

        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.SourceQuantity ==
                (expectedBagCount == 0
                    ? 0
                    : fixture.InitialSourceStack) &&
            state.OutputQuantity == 0 &&
            state.TotalBagItemCount == expectedBagCount &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 1 &&
            state.ConflictCount == 0 &&
            state.CommittedInboxCount == 0 &&
            state.RejectedInboxCount == 1 &&
            state.IsReconciled,
            $"{fixture.Family} {expectedStatus} writes evidence only");
    }
}
