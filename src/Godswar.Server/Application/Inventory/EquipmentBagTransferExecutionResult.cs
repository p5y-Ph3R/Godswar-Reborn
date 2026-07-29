using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum EquipmentBagTransferResultStatus : byte
{
    Equipped = 1,
    Unequipped = 2,
    StaleEquipment = 3,
    StaleKitBag = 4,
    BothEmpty = 5,
    BothOccupied = 6,
    ItemNotEquipment = 7,
    WrongEquipmentSlot = 8,
    ProfessionRestricted = 9,
    LevelRestricted = 10,
    MountDependencyBlocked = 11,
    MountUnsupported = 12,
    RideRuntimeBlocked = 13
}

internal sealed record EquipmentBagTransferExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public EquipmentBagTransferExecutionReceipt(
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        EquipmentBagTransferResultStatus status,
        string expectedEquipmentCompactItemState,
        string expectedKitBagCompactItemState,
        string authoritativeEquipmentCompactItemState,
        string authoritativeKitBagCompactItemState,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        ValidateSlots(equipmentSlot, kitBagSlot);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ValidateCompactState(
            expectedEquipmentCompactItemState,
            nameof(expectedEquipmentCompactItemState));
        ValidateCompactState(
            expectedKitBagCompactItemState,
            nameof(expectedKitBagCompactItemState));
        ValidateCompactState(
            authoritativeEquipmentCompactItemState,
            nameof(authoritativeEquipmentCompactItemState));
        ValidateCompactState(
            authoritativeKitBagCompactItemState,
            nameof(authoritativeKitBagCompactItemState));
        if (inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (Encoding.UTF8.GetByteCount(auditReference) >
                MaximumAuditReferenceUtf8Bytes ||
            auditReference.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference));
        }

        ValidateOutcome(
            equipmentSlot,
            status,
            expectedEquipmentCompactItemState,
            expectedKitBagCompactItemState,
            authoritativeEquipmentCompactItemState,
            authoritativeKitBagCompactItemState,
            inventoryRevision,
            outboxEventId);

        CharacterId = characterId;
        EquipmentSlot = equipmentSlot;
        KitBagSlot = kitBagSlot;
        Status = status;
        ExpectedEquipmentCompactItemState =
            expectedEquipmentCompactItemState;
        ExpectedKitBagCompactItemState =
            expectedKitBagCompactItemState;
        AuthoritativeEquipmentCompactItemState =
            authoritativeEquipmentCompactItemState;
        AuthoritativeKitBagCompactItemState =
            authoritativeKitBagCompactItemState;
        InventoryRevision = inventoryRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family =>
        CommandFamily.EquipmentBagTransfer;
    public int CharacterId { get; }
    public int EquipmentSlot { get; }
    public int KitBagSlot { get; }
    public EquipmentBagTransferResultStatus Status { get; }
    public string ExpectedEquipmentCompactItemState { get; }
    public string ExpectedKitBagCompactItemState { get; }
    public string AuthoritativeEquipmentCompactItemState { get; }
    public string AuthoritativeKitBagCompactItemState { get; }
    public long InventoryRevision { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }

    private static void ValidateSlots(
        int equipmentSlot,
        int kitBagSlot)
    {
        if (equipmentSlot is
                < EquipmentBagTransferCommandEnvelope
                    .MinimumEquipmentSlot or
                > EquipmentBagTransferCommandEnvelope
                    .MaximumEquipmentSlot ||
            kitBagSlot is
                < EquipmentBagTransferCommandEnvelope.MinimumKitBagSlot or
                > EquipmentBagTransferCommandEnvelope.MaximumKitBagSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(equipmentSlot));
        }
    }

    private static void ValidateOutcome(
        int equipmentSlot,
        EquipmentBagTransferResultStatus status,
        string expectedEquipment,
        string expectedKitBag,
        string authoritativeEquipment,
        string authoritativeKitBag,
        long revision,
        Guid? eventId)
    {
        var mutated = status is
            EquipmentBagTransferResultStatus.Equipped or
            EquipmentBagTransferResultStatus.Unequipped;
        if (mutated != eventId.HasValue ||
            (mutated && (eventId == Guid.Empty || revision <= 0)))
        {
            throw new ArgumentException(
                "Only a committed transfer has a revision and event.");
        }

        var equipmentMatches = string.Equals(
            expectedEquipment,
            authoritativeEquipment,
            StringComparison.Ordinal);
        var kitBagMatches = string.Equals(
            expectedKitBag,
            authoritativeKitBag,
            StringComparison.Ordinal);
        var expectedEquipmentEmpty = expectedEquipment == "[]";
        var expectedKitBagEmpty = expectedKitBag == "[]";
        var valid = status switch
        {
            EquipmentBagTransferResultStatus.Equipped =>
                equipmentMatches && kitBagMatches &&
                expectedEquipmentEmpty && !expectedKitBagEmpty,
            EquipmentBagTransferResultStatus.Unequipped =>
                equipmentMatches && kitBagMatches &&
                !expectedEquipmentEmpty && expectedKitBagEmpty,
            EquipmentBagTransferResultStatus.StaleEquipment =>
                !equipmentMatches,
            EquipmentBagTransferResultStatus.StaleKitBag =>
                equipmentMatches && !kitBagMatches,
            EquipmentBagTransferResultStatus.BothEmpty =>
                equipmentMatches && kitBagMatches &&
                expectedEquipmentEmpty && expectedKitBagEmpty,
            EquipmentBagTransferResultStatus.BothOccupied =>
                equipmentMatches && kitBagMatches &&
                !expectedEquipmentEmpty && !expectedKitBagEmpty,
            EquipmentBagTransferResultStatus.ItemNotEquipment or
            EquipmentBagTransferResultStatus.ProfessionRestricted or
            EquipmentBagTransferResultStatus.LevelRestricted or
            EquipmentBagTransferResultStatus.MountUnsupported =>
                equipmentMatches && kitBagMatches &&
                expectedEquipmentEmpty && !expectedKitBagEmpty,
            EquipmentBagTransferResultStatus.WrongEquipmentSlot =>
                equipmentMatches && kitBagMatches &&
                expectedEquipmentEmpty != expectedKitBagEmpty,
            EquipmentBagTransferResultStatus.MountDependencyBlocked =>
                equipmentMatches && kitBagMatches,
            EquipmentBagTransferResultStatus.RideRuntimeBlocked =>
                equipmentSlot ==
                    EquipmentBagTransferCommandEnvelope
                        .MaximumEquipmentSlot &&
                equipmentMatches && kitBagMatches &&
                expectedEquipmentEmpty != expectedKitBagEmpty,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The transfer receipt states do not match its status.");
        }
    }

    private static void ValidateCompactState(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) >
                EquipmentBagTransferCommandEnvelope
                    .MaximumExpectedStateUtf8Bytes ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']')
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal enum EquipmentBagTransferDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record EquipmentBagTransferExecutionResult
{
    public EquipmentBagTransferExecutionResult(
        EquipmentBagTransferDisposition disposition,
        EquipmentBagTransferExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }
        var requiresReceipt = disposition is
            EquipmentBagTransferDisposition.Committed or
            EquipmentBagTransferDisposition.Duplicate or
            EquipmentBagTransferDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                "The result receipt does not match its disposition.",
                nameof(receipt));
        }
        var mutation = receipt?.Status is
            EquipmentBagTransferResultStatus.Equipped or
            EquipmentBagTransferResultStatus.Unequipped;
        if (disposition == EquipmentBagTransferDisposition.Committed &&
            !mutation)
        {
            throw new ArgumentException(
                "A committed result must describe a transfer.",
                nameof(receipt));
        }
        if (disposition ==
                EquipmentBagTransferDisposition.TerminalRejected &&
            mutation)
        {
            throw new ArgumentException(
                "A terminal rejection cannot describe a transfer.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public EquipmentBagTransferDisposition Disposition { get; }
    public EquipmentBagTransferExecutionReceipt? Receipt { get; }
    public bool IsSuccess =>
        Receipt?.Status is (
            EquipmentBagTransferResultStatus.Equipped or
            EquipmentBagTransferResultStatus.Unequipped) &&
        Disposition is (
            EquipmentBagTransferDisposition.Committed or
            EquipmentBagTransferDisposition.Duplicate);
    public bool IsDurable => Receipt is not null;

    public static EquipmentBagTransferExecutionResult Committed(
        EquipmentBagTransferExecutionReceipt receipt) =>
        new(EquipmentBagTransferDisposition.Committed, receipt);
    public static EquipmentBagTransferExecutionResult Duplicate(
        EquipmentBagTransferExecutionReceipt receipt) =>
        new(EquipmentBagTransferDisposition.Duplicate, receipt);
    public static EquipmentBagTransferExecutionResult TerminalRejected(
        EquipmentBagTransferExecutionReceipt receipt) =>
        new(EquipmentBagTransferDisposition.TerminalRejected, receipt);
    public static EquipmentBagTransferExecutionResult ReplayNotFound() =>
        new(EquipmentBagTransferDisposition.ReplayNotFound);
    public static EquipmentBagTransferExecutionResult
        RequestHashConflict() =>
        new(EquipmentBagTransferDisposition.RequestHashConflict);
    public static EquipmentBagTransferExecutionResult InvalidIntent() =>
        new(EquipmentBagTransferDisposition.InvalidIntent);
    public static EquipmentBagTransferExecutionResult
        PreconditionFailed() =>
        new(EquipmentBagTransferDisposition.PreconditionFailed);
}
