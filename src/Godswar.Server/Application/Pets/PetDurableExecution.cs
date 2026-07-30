using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal enum PetDurableExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    RequestHashConflict = 4,
    InvalidIntent = 5,
    CharacterNotFound = 6
}

internal enum PetDurableReceiptStatus : byte
{
    EggHatched = 1,
    EquipmentEquipped = 2,
    PetLevelUpgraded = 3,
    PresenceChanged = 4,
    ItemNotFound = 10,
    UnsupportedItem = 11,
    EquipmentSlotOccupied = 12,
    EquipmentRestricted = 13,
    PetCapacityReached = 14,
    PetNotFound = 15,
    PetUnavailable = 16,
    PetMaximumLevel = 17,
    PetInsufficientExperience = 18,
    PetNotTaken = 19
}

internal sealed record PetDurableReceipt(
    CommandFamily Family,
    PetDurableReceiptStatus Status,
    int AccountId,
    int CharacterId,
    int KitBagSlot,
    int EquipmentSlot,
    long PetId,
    short PetLevel,
    long PetExperience,
    long PetRevision,
    bool IsCarried,
    bool IsSummoned,
    byte PresenceOperation,
    long AggregateRevision,
    string AuditReference,
    Guid? OutboxEventId)
{
    public bool Succeeded =>
        Status is PetDurableReceiptStatus.EggHatched or
            PetDurableReceiptStatus.EquipmentEquipped or
            PetDurableReceiptStatus.PetLevelUpgraded or
            PetDurableReceiptStatus.PresenceChanged;

    public void Validate()
    {
        if (Family is not (
                CommandFamily.BagItemActivation or
                CommandFamily.PetLevelUpgrade or
                CommandFamily.PetPresenceTransition) ||
            !Enum.IsDefined(Status) ||
            AccountId <= 0 ||
            CharacterId <= 0 ||
            KitBagSlot is < -1 or >
                PetDurableCommandContract.MaximumKitBagSlot ||
            EquipmentSlot is < -1 or > 20 ||
            PetId < 0 ||
            PetLevel is < 0 or > 120 ||
            PetExperience < 0 ||
            PetRevision < 0 ||
            PresenceOperation > 3 ||
            AggregateRevision < 0 ||
            string.IsNullOrWhiteSpace(AuditReference) ||
            AuditReference.Any(char.IsControl) ||
            Succeeded != (OutboxEventId is { } id && id != Guid.Empty))
        {
            throw new InvalidDataException(
                "Pet durable receipt evidence is inconsistent.");
        }
        if (!StatusMatchesFamily() ||
            (Family == CommandFamily.PetPresenceTransition) !=
                (PresenceOperation is >= 1 and <= 3) ||
            Status == PetDurableReceiptStatus.EggHatched &&
                (PetId <= 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.EquipmentEquipped &&
                (KitBagSlot < 0 || EquipmentSlot < 0) ||
            Status == PetDurableReceiptStatus.PetLevelUpgraded &&
                (PetId <= 0 || PetLevel <= 1 || PetRevision <= 0) ||
            Status == PetDurableReceiptStatus.PresenceChanged &&
                (PetId <= 0 || PetRevision <= 0))
        {
            throw new InvalidDataException(
                "Pet durable receipt status does not match its family.");
        }

    }

    private bool StatusMatchesFamily() =>
        Family switch
        {
            CommandFamily.BagItemActivation =>
                Status is PetDurableReceiptStatus.EggHatched or
                    PetDurableReceiptStatus.EquipmentEquipped or
                    PetDurableReceiptStatus.ItemNotFound or
                    PetDurableReceiptStatus.UnsupportedItem or
                    PetDurableReceiptStatus.EquipmentSlotOccupied or
                    PetDurableReceiptStatus.EquipmentRestricted or
                    PetDurableReceiptStatus.PetCapacityReached,
            CommandFamily.PetLevelUpgrade =>
                Status is PetDurableReceiptStatus.PetLevelUpgraded or
                    PetDurableReceiptStatus.PetNotFound or
                    PetDurableReceiptStatus.PetUnavailable or
                    PetDurableReceiptStatus.PetMaximumLevel or
                    PetDurableReceiptStatus.PetInsufficientExperience,
            CommandFamily.PetPresenceTransition =>
                Status is PetDurableReceiptStatus.PresenceChanged or
                    PetDurableReceiptStatus.PetNotFound or
                    PetDurableReceiptStatus.PetUnavailable or
                    PetDurableReceiptStatus.PetNotTaken,
            _ => false
        };
}

internal sealed record PetDurableExecutionResult(
    PetDurableExecutionDisposition Disposition,
    PetDurableReceipt? Receipt)
{
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is PetDurableExecutionDisposition.Committed or
            PetDurableExecutionDisposition.Duplicate;

    public static PetDurableExecutionResult Committed(
        PetDurableReceipt receipt) =>
        Create(PetDurableExecutionDisposition.Committed, receipt);

    public static PetDurableExecutionResult Duplicate(
        PetDurableReceipt receipt) =>
        Create(PetDurableExecutionDisposition.Duplicate, receipt);

    public static PetDurableExecutionResult Rejected(
        PetDurableReceipt receipt) =>
        Create(
            PetDurableExecutionDisposition.TerminalRejected,
            receipt);

    public static PetDurableExecutionResult NonDurable(
        PetDurableExecutionDisposition disposition) =>
        Create(disposition, null);

    private static PetDurableExecutionResult Create(
        PetDurableExecutionDisposition disposition,
        PetDurableReceipt? receipt)
    {
        receipt?.Validate();
        var requiresReceipt = disposition is
            PetDurableExecutionDisposition.Committed or
            PetDurableExecutionDisposition.Duplicate or
            PetDurableExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null) ||
            disposition == PetDurableExecutionDisposition.Committed &&
                receipt?.Succeeded != true ||
            disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
                receipt?.Succeeded != false)
        {
            throw new ArgumentException(
                "Pet durable execution evidence is invalid.");
        }

        return new(disposition, receipt);
    }
}
