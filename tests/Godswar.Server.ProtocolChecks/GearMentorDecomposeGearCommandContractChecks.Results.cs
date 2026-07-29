using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDecomposeGearCommandContractChecks
{
    private static readonly GearMentorDecomposeReceiptSelection[]
        ReceiptSelections =
        [
            new(12, 10001),
            new(21, 10002),
            new(30, 10003)
        ];

    private static readonly GearMentorDecomposeDustOutcome[] DustOutcomes =
    [
        new(12, 9900, 6, 1),
        new(21, 9901, 8, 0),
        new(30, 9902, 10, 1)
    ];

    private static void CheckNativeResultMapping()
    {
        var mappings = new[]
        {
            Mapping(
                GearMentorDecomposeGearResultStatus.Succeeded,
                GearEnhancerProtocol.DecomposeSucceededResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.SelectionMissing,
                GearEnhancerProtocol
                    .DecomposeNothingSelectedResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.PlayerLevelTooLow,
                GearEnhancerProtocol.DecomposePlayerLevelResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.InvalidEquipment,
                GearEnhancerProtocol
                    .DecomposeInvalidEquipmentResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.EquipmentLevelTooLow,
                GearEnhancerProtocol
                    .DecomposeEquipmentLevelResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus
                    .InsufficientEquipmentQuality,
                GearEnhancerProtocol
                    .DecomposeInsufficientQualityResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.ClassSuit,
                GearEnhancerProtocol.ClassSuitResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.InsufficientCapacity,
                GearEnhancerProtocol.BagFullResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.StaleSelection,
                GearEnhancerProtocol.SelectedItemMissingResultSubId),
            Mapping(
                GearMentorDecomposeGearResultStatus.InvalidSelection,
                GearEnhancerProtocol.InvalidSelectionResultSubId)
        };

        foreach (var mapping in mappings)
        {
            Check.Equal(
                mapping.ResultSubId,
                GearMentorDecomposeGearNativeResults.GetResultSubId(
                    mapping.Status),
                $"{mapping.Status} uses the stock Decompose result");
        }
        Check.Throws<ArgumentOutOfRangeException>(
            () =>
                GearMentorDecomposeGearNativeResults.GetResultSubId(
                    (GearMentorDecomposeGearResultStatus)byte.MaxValue),
            "Decompose rejects an unknown native status");
    }

    private static void CheckReceiptAndResultInvariants()
    {
        var success = SuccessReceipt();
        Check.Equal(
            (int)CommandFamily.GearMentorDecomposeGear,
            (int)success.Family,
            "Decompose receipt is family-specific");
        Check.Equal(
            3,
            success.DustOutcomes.Length,
            "success persists every random Dust outcome");
        for (var index = 0; index < DustOutcomes.Length; index++)
        {
            Check.Equal(
                DustOutcomes[index],
                success.DustOutcomes[index],
                $"Dust outcome {index} preserves item, quantity, and binding");
        }

        Check.True(
            GearMentorDecomposeGearExecutionResult
                .Committed(success).IsSuccess,
            "committed Decompose receipt is successful");
        Check.True(
            GearMentorDecomposeGearExecutionResult
                .Duplicate(success).IsSuccess,
            "duplicate replay uses the persisted Dust outcomes");

        foreach (var status in Enum
                     .GetValues<GearMentorDecomposeGearResultStatus>()
                     .Where(static status =>
                         status !=
                         GearMentorDecomposeGearResultStatus.Succeeded))
        {
            var rejection = RejectionReceipt(status);
            var result =
                GearMentorDecomposeGearExecutionResult.TerminalRejected(
                    rejection);
            Check.True(
                result.IsDurable && !result.IsSuccess &&
                rejection.DustOutcomes.IsEmpty,
                $"{status} is a durable rejection without random output");
        }

        Check.True(
            !GearMentorDecomposeGearExecutionResult
                .ReplayNotFound().IsDurable &&
            !GearMentorDecomposeGearExecutionResult
                .RequestHashConflict().IsDurable &&
            !GearMentorDecomposeGearExecutionResult
                .InvalidIntent().IsDurable &&
            !GearMentorDecomposeGearExecutionResult
                .PreconditionFailed().IsDurable,
            "non-durable Decompose outcomes carry no receipt");

        CheckReceiptGuards(success);
        CheckResultGuards(success);
    }

    private static void CheckReceiptGuards(
        GearMentorDecomposeGearExecutionReceipt success)
    {
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                nativeResultSubId: 1024,
                ReceiptSelections,
                DustOutcomes,
                Guid.NewGuid()),
            "receipt rejects a mismatched native result");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                selections:
                [
                    new(12, 10001),
                    new(12, 10002)
                ],
                dustOutcomes:
                [
                    new(12, 9900, 1, 0),
                    new(12, 9901, 1, 0)
                ],
                outboxEventId: Guid.NewGuid()),
            "receipt rejects duplicate source slots");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                selections: [new(12, 0)],
                dustOutcomes: [new(12, 9900, 1, 0)],
                outboxEventId: Guid.NewGuid()),
            "receipt requires exact source item identities");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                ReceiptSelections,
                DustOutcomes[..2],
                Guid.NewGuid()),
            "success requires one Dust outcome per input");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                selections: [ReceiptSelections[0]],
                dustOutcomes: [new(13, 9900, 1, 0)],
                outboxEventId: Guid.NewGuid()),
            "Dust outcomes preserve ordered source-slot correlation");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                selections: [ReceiptSelections[0]],
                dustOutcomes: [new(12, 0, 1, 0)],
                outboxEventId: Guid.NewGuid()),
            "Dust outcome requires an item identity");
        foreach (var quantity in new[] { 0, 100 })
        {
            Check.Throws<ArgumentException>(
                () => CreateReceipt(
                    GearMentorDecomposeGearResultStatus.Succeeded,
                    selections: [ReceiptSelections[0]],
                    dustOutcomes: [new(12, 9900, quantity, 0)],
                    outboxEventId: Guid.NewGuid()),
                "Dust outcome quantity is bounded");
        }
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                selections: [ReceiptSelections[0]],
                dustOutcomes: [new(12, 9900, 1, 2)],
                outboxEventId: Guid.NewGuid()),
            "Dust outcome binding is bounded");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.InvalidEquipment,
                ReceiptSelections,
                DustOutcomes,
                null),
            "rejection cannot invent random Dust outcomes");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.Succeeded,
                ReceiptSelections,
                DustOutcomes,
                null),
            "success requires an outbox event");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                GearMentorDecomposeGearResultStatus.InvalidEquipment,
                ReceiptSelections,
                [],
                Guid.NewGuid()),
            "rejection forbids an outbox event");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new GearMentorDecomposeGearExecutionReceipt(
                success.CharacterId,
                success.Status,
                success.NativeResultSubId,
                success.Selections,
                success.DustOutcomes,
                inventoryRevision: -1,
                success.AuditReference,
                success.OutboxEventId),
            "receipt rejects a negative inventory revision");
    }

    private static void CheckResultGuards(
        GearMentorDecomposeGearExecutionReceipt success)
    {
        var rejection = RejectionReceipt(
            GearMentorDecomposeGearResultStatus.InvalidEquipment);
        Check.Throws<ArgumentException>(
            () => GearMentorDecomposeGearExecutionResult
                .Committed(rejection),
            "committed disposition requires success");
        Check.Throws<ArgumentException>(
            () => GearMentorDecomposeGearExecutionResult
                .TerminalRejected(success),
            "terminal rejection forbids success");
        Check.Throws<ArgumentException>(
            () => new GearMentorDecomposeGearExecutionResult(
                GearMentorDecomposeGearExecutionDisposition.Committed),
            "durable disposition requires a receipt");
        Check.Throws<ArgumentException>(
            () => new GearMentorDecomposeGearExecutionResult(
                GearMentorDecomposeGearExecutionDisposition
                    .ReplayNotFound,
                success),
            "non-durable disposition forbids a receipt");
    }

    private static GearMentorDecomposeGearExecutionReceipt SuccessReceipt() =>
        CreateReceipt(
            GearMentorDecomposeGearResultStatus.Succeeded,
            ReceiptSelections,
            DustOutcomes,
            Guid.NewGuid());

    private static GearMentorDecomposeGearExecutionReceipt RejectionReceipt(
        GearMentorDecomposeGearResultStatus status) =>
        CreateReceipt(
            status,
            ReceiptSelections,
            [],
            null);

    private static GearMentorDecomposeGearExecutionReceipt CreateReceipt(
        GearMentorDecomposeGearResultStatus status,
        IReadOnlyList<GearMentorDecomposeReceiptSelection> selections,
        IReadOnlyList<GearMentorDecomposeDustOutcome> dustOutcomes,
        Guid? outboxEventId) =>
        CreateReceipt(
            status,
            GearMentorDecomposeGearNativeResults.GetResultSubId(status),
            selections,
            dustOutcomes,
            outboxEventId);

    private static GearMentorDecomposeGearExecutionReceipt CreateReceipt(
        GearMentorDecomposeGearResultStatus status,
        int nativeResultSubId,
        IReadOnlyList<GearMentorDecomposeReceiptSelection> selections,
        IReadOnlyList<GearMentorDecomposeDustOutcome> dustOutcomes,
        Guid? outboxEventId) =>
        new(
            characterId: 7,
            status,
            nativeResultSubId,
            selections,
            dustOutcomes,
            inventoryRevision: 9,
            auditReference: "economy-audit:test",
            outboxEventId);

    private static NativeMapping Mapping(
        GearMentorDecomposeGearResultStatus status,
        int resultSubId) =>
        new(status, resultSubId);

    private sealed record NativeMapping(
        GearMentorDecomposeGearResultStatus Status,
        int ResultSubId);
}
