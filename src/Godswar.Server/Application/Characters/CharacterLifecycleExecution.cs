using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Characters;

internal enum CharacterLifecycleExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    RequestHashConflict = 4,
    InvalidIntent = 5,
    AccountNotFound = 6
}

internal enum CharacterLifecycleReceiptStatus : byte
{
    Created = 1,
    Deleted = 2,
    Restored = 3,
    Purged = 4,
    SlotOccupied = 5,
    NameUnavailable = 6,
    CharacterNotFound = 7,
    NameMismatch = 8,
    StaleLifecycleVersion = 9,
    InvalidLifecycleState = 10,
    RestoreExpired = 11,
    RestoreBlockedByActiveSlot = 12,
    PurgeNotEligible = 13,
    CharacterInUse = 14
}

internal sealed record CharacterLifecycleReceipt
{
    public CharacterLifecycleReceipt(
        CommandFamily family,
        CharacterLifecycleReceiptStatus status,
        int accountId,
        short characterSlot,
        int characterId,
        long lifecycleVersion,
        string characterName,
        DateTimeOffset? restoreUntil,
        DateTimeOffset? purgeAfter,
        string auditReference,
        Guid? outboxEventId)
    {
        if (family is not (
                CommandFamily.CharacterCreate or
                CommandFamily.CharacterDelete or
                CommandFamily.CharacterRestore or
                CommandFamily.CharacterPurge) ||
            !Enum.IsDefined(status) ||
            !IsStatusValidForFamily(family, status) ||
            accountId <= 0 ||
            characterSlot !=
                CharacterLifecycleCommandContract.SingleCharacterSlot ||
            characterId < 0 ||
            lifecycleVersion < 0 ||
            characterName is null ||
            characterName.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(characterName) >
                CharacterLifecycleCommandContract.MaximumNameUtf8Bytes ||
            string.IsNullOrWhiteSpace(auditReference) ||
            auditReference.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(auditReference) > 256)
        {
            throw new ArgumentException(
                "Character lifecycle receipt evidence is invalid.");
        }

        var succeeded = IsSuccessStatus(status);
        var hasValidEventId =
            outboxEventId is { } eventId && eventId != Guid.Empty;
        if (succeeded &&
                (characterId <= 0 ||
                 lifecycleVersion <= 0 ||
                 !CharacterLifecycleCommandContract.IsValidName(
                     characterName) ||
                 !hasValidEventId) ||
            !succeeded && outboxEventId is not null ||
            !HasValidTimestampShape(
                family,
                status,
                restoreUntil,
                purgeAfter))
        {
            throw new ArgumentException(
                "Character lifecycle receipt mutation evidence is inconsistent.");
        }

        Family = family;
        Status = status;
        AccountId = accountId;
        CharacterSlot = characterSlot;
        CharacterId = characterId;
        LifecycleVersion = lifecycleVersion;
        CharacterName = characterName;
        RestoreUntil = restoreUntil;
        PurgeAfter = purgeAfter;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family { get; }
    public CharacterLifecycleReceiptStatus Status { get; }
    public int AccountId { get; }
    public short CharacterSlot { get; }
    public int CharacterId { get; }
    public long LifecycleVersion { get; }
    public string CharacterName { get; }
    public DateTimeOffset? RestoreUntil { get; }
    public DateTimeOffset? PurgeAfter { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }
    public bool Succeeded => IsSuccessStatus(Status);

    private static bool IsSuccessStatus(
        CharacterLifecycleReceiptStatus status) =>
        status is
            CharacterLifecycleReceiptStatus.Created or
            CharacterLifecycleReceiptStatus.Deleted or
            CharacterLifecycleReceiptStatus.Restored or
            CharacterLifecycleReceiptStatus.Purged;

    private static bool IsStatusValidForFamily(
        CommandFamily family,
        CharacterLifecycleReceiptStatus status) =>
        family switch
        {
            CommandFamily.CharacterCreate =>
                status is CharacterLifecycleReceiptStatus.Created or
                    CharacterLifecycleReceiptStatus.SlotOccupied or
                    CharacterLifecycleReceiptStatus.NameUnavailable,
            CommandFamily.CharacterDelete =>
                status is CharacterLifecycleReceiptStatus.Deleted or
                    CharacterLifecycleReceiptStatus.CharacterNotFound or
                    CharacterLifecycleReceiptStatus.NameMismatch or
                    CharacterLifecycleReceiptStatus.CharacterInUse or
                    CharacterLifecycleReceiptStatus
                        .StaleLifecycleVersion,
            CommandFamily.CharacterRestore =>
                status is CharacterLifecycleReceiptStatus.Restored or
                    CharacterLifecycleReceiptStatus.CharacterNotFound or
                    CharacterLifecycleReceiptStatus
                        .StaleLifecycleVersion or
                    CharacterLifecycleReceiptStatus
                        .InvalidLifecycleState or
                    CharacterLifecycleReceiptStatus.RestoreExpired or
                    CharacterLifecycleReceiptStatus
                        .RestoreBlockedByActiveSlot,
            CommandFamily.CharacterPurge =>
                status is CharacterLifecycleReceiptStatus.Purged or
                    CharacterLifecycleReceiptStatus.CharacterNotFound or
                    CharacterLifecycleReceiptStatus
                        .StaleLifecycleVersion or
                    CharacterLifecycleReceiptStatus
                        .InvalidLifecycleState or
                    CharacterLifecycleReceiptStatus.PurgeNotEligible,
            _ => false
        };

    private static bool HasValidTimestampShape(
        CommandFamily family,
        CharacterLifecycleReceiptStatus status,
        DateTimeOffset? restoreUntil,
        DateTimeOffset? purgeAfter)
    {
        var hasPair = restoreUntil.HasValue && purgeAfter.HasValue;
        if (restoreUntil.HasValue != purgeAfter.HasValue ||
            hasPair && restoreUntil > purgeAfter)
        {
            return false;
        }

        return status switch
        {
            CharacterLifecycleReceiptStatus.Deleted or
            CharacterLifecycleReceiptStatus.Purged or
            CharacterLifecycleReceiptStatus.RestoreExpired or
            CharacterLifecycleReceiptStatus
                .RestoreBlockedByActiveSlot or
            CharacterLifecycleReceiptStatus.PurgeNotEligible =>
                hasPair,
            CharacterLifecycleReceiptStatus.StaleLifecycleVersion =>
                !hasPair ||
                family is CommandFamily.CharacterRestore or
                    CommandFamily.CharacterPurge,
            _ => !hasPair
        };
    }
}

internal sealed record CharacterLifecycleExecutionResult
{
    private CharacterLifecycleExecutionResult(
        CharacterLifecycleExecutionDisposition disposition,
        CharacterLifecycleReceipt? receipt)
    {
        if (!Enum.IsDefined(disposition) ||
            disposition switch
            {
                CharacterLifecycleExecutionDisposition.Committed =>
                    receipt?.Succeeded != true,
                CharacterLifecycleExecutionDisposition.Duplicate =>
                    receipt is null,
                CharacterLifecycleExecutionDisposition.TerminalRejected =>
                    receipt is null || receipt.Succeeded,
                CharacterLifecycleExecutionDisposition
                    .RequestHashConflict or
                CharacterLifecycleExecutionDisposition.InvalidIntent or
                CharacterLifecycleExecutionDisposition.AccountNotFound =>
                    receipt is not null,
                _ => true
            })
        {
            throw new ArgumentException(
                "Character lifecycle execution evidence is inconsistent.");
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public CharacterLifecycleExecutionDisposition Disposition { get; }

    public CharacterLifecycleReceipt? Receipt { get; }

    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is
            CharacterLifecycleExecutionDisposition.Committed or
            CharacterLifecycleExecutionDisposition.Duplicate;

    public static CharacterLifecycleExecutionResult Committed(
        CharacterLifecycleReceipt receipt) =>
        new(CharacterLifecycleExecutionDisposition.Committed, receipt);

    public static CharacterLifecycleExecutionResult Duplicate(
        CharacterLifecycleReceipt receipt) =>
        new(CharacterLifecycleExecutionDisposition.Duplicate, receipt);

    public static CharacterLifecycleExecutionResult TerminalRejected(
        CharacterLifecycleReceipt receipt) =>
        new(
            CharacterLifecycleExecutionDisposition.TerminalRejected,
            receipt);

    public static CharacterLifecycleExecutionResult RequestHashConflict() =>
        new(
            CharacterLifecycleExecutionDisposition.RequestHashConflict,
            null);

    public static CharacterLifecycleExecutionResult InvalidIntent() =>
        new(CharacterLifecycleExecutionDisposition.InvalidIntent, null);

    public static CharacterLifecycleExecutionResult AccountNotFound() =>
        new(CharacterLifecycleExecutionDisposition.AccountNotFound, null);
}
