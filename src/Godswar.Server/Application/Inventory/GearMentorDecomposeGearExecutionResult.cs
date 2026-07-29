using System.Collections.Immutable;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum GearMentorDecomposeGearResultStatus : byte
{
    Succeeded = 1,
    SelectionMissing = 2,
    PlayerLevelTooLow = 3,
    InvalidEquipment = 4,
    EquipmentLevelTooLow = 5,
    InsufficientEquipmentQuality = 6,
    ClassSuit = 7,
    InsufficientCapacity = 8,
    StaleSelection = 9,
    InvalidSelection = 10
}

internal static class GearMentorDecomposeGearNativeResults
{
    public const int SucceededSubId = 1005;
    public const int SelectionMissingSubId = 1024;
    public const int PlayerLevelTooLowSubId = 1015;
    public const int InvalidEquipmentSubId = 1003;
    public const int EquipmentLevelTooLowSubId = 1014;
    public const int InsufficientEquipmentQualitySubId = 1004;
    public const int ClassSuitSubId = 1032;
    public const int InsufficientCapacitySubId = 1020;
    public const int StaleSelectionSubId = 1002;
    public const int InvalidSelectionSubId = 1019;

    public static int GetResultSubId(
        GearMentorDecomposeGearResultStatus status) =>
        status switch
        {
            GearMentorDecomposeGearResultStatus.Succeeded =>
                SucceededSubId,
            GearMentorDecomposeGearResultStatus.SelectionMissing =>
                SelectionMissingSubId,
            GearMentorDecomposeGearResultStatus.PlayerLevelTooLow =>
                PlayerLevelTooLowSubId,
            GearMentorDecomposeGearResultStatus.InvalidEquipment =>
                InvalidEquipmentSubId,
            GearMentorDecomposeGearResultStatus.EquipmentLevelTooLow =>
                EquipmentLevelTooLowSubId,
            GearMentorDecomposeGearResultStatus
                .InsufficientEquipmentQuality =>
                InsufficientEquipmentQualitySubId,
            GearMentorDecomposeGearResultStatus.ClassSuit =>
                ClassSuitSubId,
            GearMentorDecomposeGearResultStatus.InsufficientCapacity =>
                InsufficientCapacitySubId,
            GearMentorDecomposeGearResultStatus.StaleSelection =>
                StaleSelectionSubId,
            GearMentorDecomposeGearResultStatus.InvalidSelection =>
                InvalidSelectionSubId,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}

internal readonly record struct GearMentorDecomposeReceiptSelection(
    int SelectedKitBagSlot,
    uint SourceItemId);

internal readonly record struct GearMentorDecomposeDustOutcome(
    int SelectedKitBagSlot,
    uint DustItemId,
    int Quantity,
    short Bound);

internal sealed record GearMentorDecomposeGearExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;
    public const int MaximumDustQuantity = 99;

    public GearMentorDecomposeGearExecutionReceipt(
        int characterId,
        GearMentorDecomposeGearResultStatus status,
        int nativeResultSubId,
        IReadOnlyList<GearMentorDecomposeReceiptSelection> selections,
        IReadOnlyList<GearMentorDecomposeDustOutcome> dustOutcomes,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (nativeResultSubId !=
            GearMentorDecomposeGearNativeResults.GetResultSubId(status))
        {
            throw new ArgumentException(
                "The native result does not match the Decompose status.",
                nameof(nativeResultSubId));
        }

        Selections = CopyAndValidateSelections(selections);
        DustOutcomes = CopyAndValidateDustOutcomes(
            status,
            Selections,
            dustOutcomes);
        if (inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        AuditReference = RequireAuditReference(auditReference);
        var succeeded =
            status == GearMentorDecomposeGearResultStatus.Succeeded;
        if (succeeded)
        {
            if (!outboxEventId.HasValue ||
                outboxEventId.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "A successful Decompose mutation requires an outbox " +
                    "event.",
                    nameof(outboxEventId));
            }
        }
        else if (outboxEventId is not null)
        {
            throw new ArgumentException(
                "A rejected Decompose command cannot publish an inventory " +
                "event.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        Status = status;
        NativeResultSubId = nativeResultSubId;
        InventoryRevision = inventoryRevision;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family => CommandFamily.GearMentorDecomposeGear;

    public int CharacterId { get; }

    public GearMentorDecomposeGearResultStatus Status { get; }

    public int NativeResultSubId { get; }

    public ImmutableArray<GearMentorDecomposeReceiptSelection> Selections
    {
        get;
    }

    public ImmutableArray<GearMentorDecomposeDustOutcome> DustOutcomes
    {
        get;
    }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid? OutboxEventId { get; }

    private static ImmutableArray<GearMentorDecomposeReceiptSelection>
        CopyAndValidateSelections(
            IReadOnlyList<GearMentorDecomposeReceiptSelection>? selections)
    {
        if (selections is null ||
            selections.Count is
                < GearMentorDecomposeGearCommandEnvelope
                    .MinimumSelectionCount or
                > GearMentorDecomposeGearCommandEnvelope
                    .MaximumSelectionCount)
        {
            throw new ArgumentException(
                "A Decompose receipt requires one to three selections.",
                nameof(selections));
        }

        var copy = ImmutableArray.CreateRange(selections);
        Span<bool> occupiedSlots = stackalloc bool[
            GearMentorDecomposeGearCommandEnvelope.MaximumKitBagSlot + 1];
        occupiedSlots.Clear();
        foreach (var selection in copy)
        {
            if (selection.SelectedKitBagSlot is
                    < GearMentorDecomposeGearCommandEnvelope
                        .MinimumKitBagSlot or
                    > GearMentorDecomposeGearCommandEnvelope
                        .MaximumKitBagSlot ||
                occupiedSlots[selection.SelectedKitBagSlot] ||
                selection.SourceItemId == 0)
            {
                throw new ArgumentException(
                    "Receipt selections require distinct valid slots and " +
                    "known source items.",
                    nameof(selections));
            }

            occupiedSlots[selection.SelectedKitBagSlot] = true;
        }

        return copy;
    }

    private static ImmutableArray<GearMentorDecomposeDustOutcome>
        CopyAndValidateDustOutcomes(
            GearMentorDecomposeGearResultStatus status,
            ImmutableArray<GearMentorDecomposeReceiptSelection> selections,
            IReadOnlyList<GearMentorDecomposeDustOutcome>? dustOutcomes)
    {
        ArgumentNullException.ThrowIfNull(dustOutcomes);
        var copy = ImmutableArray.CreateRange(dustOutcomes);
        var succeeded =
            status == GearMentorDecomposeGearResultStatus.Succeeded;
        if (!succeeded)
        {
            if (!copy.IsEmpty)
            {
                throw new ArgumentException(
                    "A rejected Decompose receipt cannot contain Dust.",
                    nameof(dustOutcomes));
            }

            return copy;
        }

        if (copy.Length != selections.Length)
        {
            throw new ArgumentException(
                "A successful receipt requires one exact Dust outcome per " +
                "selected gear item.",
                nameof(dustOutcomes));
        }

        for (var index = 0; index < copy.Length; index++)
        {
            var outcome = copy[index];
            if (outcome.SelectedKitBagSlot !=
                    selections[index].SelectedKitBagSlot ||
                outcome.DustItemId == 0 ||
                outcome.Quantity is < 1 or > MaximumDustQuantity ||
                outcome.Bound is < 0 or > 1)
            {
                throw new ArgumentException(
                    "Dust outcomes must preserve selection order and exact " +
                    "item, quantity, and binding.",
                    nameof(dustOutcomes));
            }
        }

        return copy;
    }

    private static string RequireAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes ||
            value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

internal enum GearMentorDecomposeGearExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record GearMentorDecomposeGearExecutionResult
{
    public GearMentorDecomposeGearExecutionResult(
        GearMentorDecomposeGearExecutionDisposition disposition,
        GearMentorDecomposeGearExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is
                GearMentorDecomposeGearExecutionDisposition.Committed or
                GearMentorDecomposeGearExecutionDisposition.Duplicate or
                GearMentorDecomposeGearExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Durable outcomes require their canonical receipt."
                    : "Non-durable outcomes cannot carry a receipt.",
                nameof(receipt));
        }
        if (disposition ==
                GearMentorDecomposeGearExecutionDisposition.Committed &&
            receipt!.Status !=
                GearMentorDecomposeGearResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "A committed result must describe success.",
                nameof(receipt));
        }
        if (disposition ==
                GearMentorDecomposeGearExecutionDisposition
                    .TerminalRejected &&
            receipt!.Status ==
                GearMentorDecomposeGearResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "A terminal rejection cannot describe success.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public GearMentorDecomposeGearExecutionDisposition Disposition { get; }

    public GearMentorDecomposeGearExecutionReceipt? Receipt { get; }

    public bool IsSuccess =>
        Receipt?.Status ==
            GearMentorDecomposeGearResultStatus.Succeeded &&
        Disposition is
            GearMentorDecomposeGearExecutionDisposition.Committed or
            GearMentorDecomposeGearExecutionDisposition.Duplicate;

    public bool IsDurable => Receipt is not null;

    public static GearMentorDecomposeGearExecutionResult Committed(
        GearMentorDecomposeGearExecutionReceipt receipt) =>
        new(
            GearMentorDecomposeGearExecutionDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static GearMentorDecomposeGearExecutionResult Duplicate(
        GearMentorDecomposeGearExecutionReceipt receipt) =>
        new(
            GearMentorDecomposeGearExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static GearMentorDecomposeGearExecutionResult TerminalRejected(
        GearMentorDecomposeGearExecutionReceipt receipt) =>
        new(
            GearMentorDecomposeGearExecutionDisposition.TerminalRejected,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static GearMentorDecomposeGearExecutionResult ReplayNotFound() =>
        new(GearMentorDecomposeGearExecutionDisposition.ReplayNotFound);

    public static GearMentorDecomposeGearExecutionResult
        RequestHashConflict() =>
        new(
            GearMentorDecomposeGearExecutionDisposition
                .RequestHashConflict);

    public static GearMentorDecomposeGearExecutionResult InvalidIntent() =>
        new(GearMentorDecomposeGearExecutionDisposition.InvalidIntent);

    public static GearMentorDecomposeGearExecutionResult
        PreconditionFailed() =>
        new(
            GearMentorDecomposeGearExecutionDisposition
                .PreconditionFailed);
}
