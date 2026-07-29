using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    GearMentorMaterialConversionCommandContractChecks
{
    private static void CheckNativeResultMapping()
    {
        var mappings = new[]
        {
            Mapping(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.Succeeded,
                1823),
            Mapping(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.InvalidCrystal,
                1822),
            Mapping(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity,
                1020),
            Mapping(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.StaleSelection,
                1822),
            Mapping(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.InvalidKitBagSlot,
                1822),
            Mapping(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.Succeeded,
                304),
            Mapping(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.InvalidGemPieces,
                301),
            Mapping(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus
                    .InsufficientGemPieces,
                302),
            Mapping(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity,
                303),
            Mapping(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.StaleSelection,
                301),
            Mapping(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.InvalidKitBagSlot,
                301)
        };

        foreach (var mapping in mappings)
        {
            Check.Equal(
                mapping.ResultSubId,
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    mapping.Family,
                    mapping.Status),
                $"{mapping.Family}/{mapping.Status} native result");
        }
        Check.Throws<ArgumentOutOfRangeException>(
            () =>
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    CommandFamily.GearMentorTransformCrystal,
                    GearMentorMaterialConversionResultStatus
                        .InvalidGemPieces),
            "Transform rejects a Combine-only durable status");
        Check.Throws<ArgumentOutOfRangeException>(
            () =>
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    CommandFamily.GearMentorCombineGemPieces,
                    GearMentorMaterialConversionResultStatus
                        .InvalidCrystal),
            "Combine rejects a Transform-only durable status");
    }

    private static void CheckReceiptAndResultInvariants()
    {
        var transformSuccess = SuccessReceipt(
            CommandFamily.GearMentorTransformCrystal,
            sourceItemId: 4234,
            outputItemId: 4233,
            outputQuantity: 2);
        var combineSuccess = SuccessReceipt(
            CommandFamily.GearMentorCombineGemPieces,
            sourceItemId: 4216,
            outputItemId: 4215,
            outputQuantity: 1);

        foreach (var receipt in new[]
                 {
                     transformSuccess,
                     combineSuccess
                 })
        {
            Check.True(
                GearMentorMaterialConversionExecutionResult
                    .Committed(receipt).IsSuccess,
                $"{receipt.Family} committed receipt is successful");
            Check.True(
                GearMentorMaterialConversionExecutionResult
                    .Duplicate(receipt).IsSuccess,
                $"{receipt.Family} duplicate receipt replays success");
        }

        var invalidCrystal = RejectionReceipt(
            CommandFamily.GearMentorTransformCrystal,
            GearMentorMaterialConversionResultStatus.InvalidCrystal,
            sourceItemId: 4215,
            outputItemId: 0,
            outputQuantity: 0,
            isBound: false);
        var insufficientPieces = RejectionReceipt(
            CommandFamily.GearMentorCombineGemPieces,
            GearMentorMaterialConversionResultStatus
                .InsufficientGemPieces,
            sourceItemId: 4216,
            outputItemId: 4215,
            outputQuantity: 1,
            isBound: true);
        foreach (var receipt in new[]
                 {
                     invalidCrystal,
                     insufficientPieces
                 })
        {
            var result =
                GearMentorMaterialConversionExecutionResult
                    .TerminalRejected(receipt);
            Check.True(
                result.IsDurable && !result.IsSuccess,
                $"{receipt.Family}/{receipt.Status} is durable rejection");
        }

        Check.True(
            !GearMentorMaterialConversionExecutionResult
                .ReplayNotFound().IsDurable &&
            !GearMentorMaterialConversionExecutionResult
                .RequestHashConflict().IsDurable &&
            !GearMentorMaterialConversionExecutionResult
                .InvalidIntent().IsDurable &&
            !GearMentorMaterialConversionExecutionResult
                .PreconditionFailed().IsDurable,
            "non-terminal provider/intent outcomes carry no receipt");

        Check.Throws<ArgumentException>(
            () => new GearMentorMaterialConversionExecutionReceipt(
                CommandFamily.GearMentorTransformCrystal,
                7,
                GearMentorMaterialConversionResultStatus.InvalidGemPieces,
                1822,
                12,
                4234,
                0,
                0,
                true,
                1,
                "audit",
                null),
            "receipt rejects a status from the wrong family");
        Check.Throws<ArgumentException>(
            () => new GearMentorMaterialConversionExecutionReceipt(
                CommandFamily.GearMentorTransformCrystal,
                7,
                GearMentorMaterialConversionResultStatus.InvalidCrystal,
                1823,
                12,
                4234,
                0,
                0,
                true,
                1,
                "audit",
                null),
            "receipt rejects mismatched native result");
        Check.Throws<ArgumentException>(
            () => SuccessReceipt(
                CommandFamily.GearMentorTransformCrystal,
                4234,
                4233,
                2,
                outboxEventId: Guid.Empty),
            "successful receipt requires an outbox event");
        Check.Throws<ArgumentException>(
            () => RejectionReceipt(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.InvalidCrystal,
                4234,
                0,
                0,
                true,
                outboxEventId: Guid.NewGuid()),
            "rejected receipt forbids an outbox event");
        Check.Throws<ArgumentException>(
            () => RejectionReceipt(
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.InvalidCrystal,
                0,
                0,
                0,
                null),
            "known invalid source requires its item identity");
        Check.Throws<ArgumentException>(
            () => RejectionReceipt(
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.StaleSelection,
                4216,
                0,
                0,
                true),
            "stale selection cannot claim item identity");
        Check.Throws<ArgumentException>(
            () => SuccessReceipt(
                CommandFamily.GearMentorTransformCrystal,
                4234,
                4233,
                0),
            "known transform output requires a positive quantity");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new GearMentorMaterialConversionExecutionReceipt(
                CommandFamily.GearMentorTransformCrystal,
                7,
                GearMentorMaterialConversionResultStatus.StaleSelection,
                1822,
                12,
                0,
                0,
                0,
                null,
                -1,
                "audit",
                null),
            "receipt rejects a negative inventory revision");
        Check.Throws<ArgumentException>(
            () => GearMentorMaterialConversionExecutionResult
                .Committed(invalidCrystal),
            "committed disposition requires a success receipt");
        Check.Throws<ArgumentException>(
            () => GearMentorMaterialConversionExecutionResult
                .TerminalRejected(transformSuccess),
            "terminal rejection forbids a success receipt");
        Check.Throws<ArgumentException>(
            () => new GearMentorMaterialConversionExecutionResult(
                GearMentorMaterialConversionExecutionDisposition.Committed),
            "durable disposition requires a receipt");
        Check.Throws<ArgumentException>(
            () => new GearMentorMaterialConversionExecutionResult(
                GearMentorMaterialConversionExecutionDisposition
                    .ReplayNotFound,
                transformSuccess),
            "non-durable disposition forbids a receipt");
    }

    private static GearMentorMaterialConversionExecutionReceipt
        SuccessReceipt(
            CommandFamily family,
            uint sourceItemId,
            uint outputItemId,
            int outputQuantity,
            Guid? outboxEventId = null) =>
        new(
            family,
            characterId: 7,
            GearMentorMaterialConversionResultStatus.Succeeded,
            GearMentorMaterialConversionNativeResults.GetResultSubId(
                family,
                GearMentorMaterialConversionResultStatus.Succeeded),
            selectedKitBagSlot: 12,
            sourceItemId,
            outputItemId,
            outputQuantity,
            isBound: true,
            inventoryRevision: 9,
            auditReference: "economy-audit:test",
            outboxEventId ?? Guid.NewGuid());

    private static GearMentorMaterialConversionExecutionReceipt
        RejectionReceipt(
            CommandFamily family,
            GearMentorMaterialConversionResultStatus status,
            uint sourceItemId,
            uint outputItemId,
            int outputQuantity,
            bool? isBound,
            Guid? outboxEventId = null) =>
        new(
            family,
            characterId: 7,
            status,
            GearMentorMaterialConversionNativeResults.GetResultSubId(
                family,
                status),
            selectedKitBagSlot: 12,
            sourceItemId,
            outputItemId,
            outputQuantity,
            isBound,
            inventoryRevision: 9,
            auditReference: "economy-audit:test",
            outboxEventId);

    private static ResultMapping Mapping(
        CommandFamily family,
        GearMentorMaterialConversionResultStatus status,
        int resultSubId) =>
        new(family, status, resultSubId);

    private sealed record ResultMapping(
        CommandFamily Family,
        GearMentorMaterialConversionResultStatus Status,
        int ResultSubId);
}
