using System.Collections.Immutable;
using System.Text;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct EquipmentForgeReceiptMaterial(
    EquipmentForgeCommandItemRole Role,
    int KitBagSlot,
    uint ItemId,
    int Quantity,
    short StackBefore,
    short StackAfter);

internal sealed record EquipmentForgeExecutionReceipt
{
    public const int MaximumCompactItemStateUtf8Bytes = 512;
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public EquipmentForgeExecutionReceipt(
        int characterId,
        EquipmentForgeCommandResultStatus status,
        int materialType,
        int roll,
        int successProbability,
        int silverSpent,
        string equipmentBeforeCompactItemState,
        string equipmentAfterCompactItemState,
        IReadOnlyList<EquipmentForgeReceiptMaterial> materials,
        long walletRevision,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0 ||
            !Enum.IsDefined(status) ||
            materialType is < 0 or > 3 ||
            successProbability is < 0 or > 100 ||
            silverSpent < 0 ||
            walletRevision < 0 ||
            inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterId),
                "The forge receipt contains an invalid scalar value.");
        }

        var committed = status is
            EquipmentForgeCommandResultStatus.Succeeded or
            EquipmentForgeCommandResultStatus.FailedRoll;
        if (committed != (materialType is >= 1 and <= 3) ||
            committed != (roll is >= 0 and <= 99) ||
            committed != (outboxEventId is { } eventId &&
                eventId != Guid.Empty) ||
            committed && inventoryRevision == 0)
        {
            throw new ArgumentException(
                "Committed forge receipts require roll, revision, and event evidence.");
        }
        if (!committed &&
            (materialType != 0 ||
             successProbability != 0 ||
             silverSpent != 0 ||
             !string.IsNullOrEmpty(equipmentBeforeCompactItemState) ||
             !string.IsNullOrEmpty(equipmentAfterCompactItemState) ||
             materials.Count != 0))
        {
            throw new ArgumentException(
                "A rejected forge receipt cannot contain mutation evidence.");
        }
        if (committed &&
            (!IsBoundedState(equipmentBeforeCompactItemState) ||
             !IsBoundedState(equipmentAfterCompactItemState)))
        {
            throw new ArgumentException(
                "Committed forge equipment evidence is invalid.");
        }
        if (committed)
        {
            if (status == EquipmentForgeCommandResultStatus.Succeeded
                    ? string.Equals(
                        equipmentBeforeCompactItemState,
                        equipmentAfterCompactItemState,
                        StringComparison.Ordinal)
                    : !string.Equals(
                        equipmentBeforeCompactItemState,
                        equipmentAfterCompactItemState,
                        StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The forge outcome contradicts its equipment evidence.");
            }
        }

        Materials = CopyAndValidateMaterials(committed, materials);
        AuditReference = RequireAuditReference(auditReference);
        CharacterId = characterId;
        Status = status;
        MaterialType = materialType;
        Roll = roll;
        SuccessProbability = successProbability;
        SilverSpent = silverSpent;
        EquipmentBeforeCompactItemState =
            equipmentBeforeCompactItemState;
        EquipmentAfterCompactItemState =
            equipmentAfterCompactItemState;
        WalletRevision = walletRevision;
        InventoryRevision = inventoryRevision;
        OutboxEventId = outboxEventId;
    }

    public int CharacterId { get; }
    public EquipmentForgeCommandResultStatus Status { get; }
    public int MaterialType { get; }
    public int Roll { get; }
    public int SuccessProbability { get; }
    public int Probability => SuccessProbability;
    public int SilverSpent { get; }
    public string EquipmentBeforeCompactItemState { get; }
    public string EquipmentAfterCompactItemState { get; }
    public ImmutableArray<EquipmentForgeReceiptMaterial> Materials { get; }
    public long WalletRevision { get; }
    public long InventoryRevision { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }
    public bool Succeeded =>
        Status == EquipmentForgeCommandResultStatus.Succeeded;
    public bool Committed =>
        Status is EquipmentForgeCommandResultStatus.Succeeded or
            EquipmentForgeCommandResultStatus.FailedRoll;

    private static ImmutableArray<EquipmentForgeReceiptMaterial>
        CopyAndValidateMaterials(
            bool committed,
            IReadOnlyList<EquipmentForgeReceiptMaterial>? materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var copy = ImmutableArray.CreateRange(materials);
        if (!committed)
        {
            return copy;
        }
        if (copy.IsEmpty ||
            copy[0].Role !=
                EquipmentForgeCommandItemRole.PrimaryMaterial ||
            copy.Skip(1).Any(static item =>
                item.Role !=
                    EquipmentForgeCommandItemRole.OddsMaterial) ||
            copy.Select(static item => item.KitBagSlot)
                .Distinct()
                .Count() != copy.Length)
        {
            throw new ArgumentException(
                "Forge materials must be in primary-then-odds order.",
                nameof(materials));
        }

        foreach (var material in copy)
        {
            if (material.KitBagSlot is
                    < EquipmentForgeCommandEnvelope.MinimumKitBagSlot or
                    > EquipmentForgeCommandEnvelope.MaximumKitBagSlot ||
                material.ItemId == 0 ||
                material.Quantity <= 0 ||
                material.StackBefore < material.Quantity ||
                material.StackAfter !=
                    material.StackBefore - material.Quantity)
            {
                throw new ArgumentException(
                    "The forge material evidence is invalid.",
                    nameof(materials));
            }
        }

        return copy;
    }

    private static bool IsBoundedState(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(value) <=
            MaximumCompactItemStateUtf8Bytes;

    private static string RequireAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

internal enum EquipmentForgeExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record EquipmentForgeExecutionResult
{
    private EquipmentForgeExecutionResult(
        EquipmentForgeExecutionDisposition disposition,
        EquipmentForgeExecutionReceipt? receipt = null)
    {
        var requiresReceipt = disposition is
            EquipmentForgeExecutionDisposition.Committed or
            EquipmentForgeExecutionDisposition.Duplicate or
            EquipmentForgeExecutionDisposition.TerminalRejected;
        if (!Enum.IsDefined(disposition) ||
            requiresReceipt != (receipt is not null) ||
            disposition ==
                EquipmentForgeExecutionDisposition.Committed &&
                !receipt!.Committed ||
            disposition ==
                EquipmentForgeExecutionDisposition.TerminalRejected &&
                receipt!.Committed)
        {
            throw new ArgumentException(
                "The forge execution disposition and receipt are inconsistent.");
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public EquipmentForgeExecutionDisposition Disposition { get; }
    public EquipmentForgeExecutionReceipt? Receipt { get; }
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is EquipmentForgeExecutionDisposition.Committed or
            EquipmentForgeExecutionDisposition.Duplicate;

    public static EquipmentForgeExecutionResult Committed(
        EquipmentForgeExecutionReceipt receipt) =>
        new(EquipmentForgeExecutionDisposition.Committed, receipt);
    public static EquipmentForgeExecutionResult Duplicate(
        EquipmentForgeExecutionReceipt receipt) =>
        new(EquipmentForgeExecutionDisposition.Duplicate, receipt);
    public static EquipmentForgeExecutionResult TerminalRejected(
        EquipmentForgeExecutionReceipt receipt) =>
        new(EquipmentForgeExecutionDisposition.TerminalRejected, receipt);
    public static EquipmentForgeExecutionResult ReplayNotFound() =>
        new(EquipmentForgeExecutionDisposition.ReplayNotFound);
    public static EquipmentForgeExecutionResult RequestHashConflict() =>
        new(EquipmentForgeExecutionDisposition.RequestHashConflict);
    public static EquipmentForgeExecutionResult InvalidIntent() =>
        new(EquipmentForgeExecutionDisposition.InvalidIntent);
    public static EquipmentForgeExecutionResult PreconditionFailed() =>
        new(EquipmentForgeExecutionDisposition.PreconditionFailed);
}
