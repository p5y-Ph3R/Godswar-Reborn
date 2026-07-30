using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleCommandContractChecks
{
    private static void CheckReceiptEvidenceContracts()
    {
        var restoreUntil = DateTimeOffset.UtcNow.AddDays(30);
        var purgeAfter = restoreUntil.AddDays(7);

        Check.Throws<ArgumentException>(
            () => Receipt(
                CommandFamily.CharacterDelete,
                CharacterLifecycleReceiptStatus.Created,
                null,
                null,
                Guid.NewGuid()),
            "a delete receipt cannot claim a create result");
        Check.Throws<ArgumentException>(
            () => Receipt(
                CommandFamily.CharacterCreate,
                CharacterLifecycleReceiptStatus.CharacterNotFound),
            "a create receipt cannot claim a delete rejection");
        Check.Throws<ArgumentException>(
            () => Receipt(
                CommandFamily.CharacterDelete,
                CharacterLifecycleReceiptStatus.Deleted,
                restoreUntil,
                null,
                Guid.NewGuid()),
            "deleted evidence requires both retention timestamps");
        Check.Throws<ArgumentException>(
            () => Receipt(
                CommandFamily.CharacterDelete,
                CharacterLifecycleReceiptStatus.Deleted,
                purgeAfter,
                restoreUntil,
                Guid.NewGuid()),
            "deleted evidence rejects reversed retention timestamps");
        Check.Throws<ArgumentException>(
            () => Receipt(
                CommandFamily.CharacterCreate,
                CharacterLifecycleReceiptStatus.Created,
                restoreUntil,
                purgeAfter,
                Guid.NewGuid()),
            "created evidence cannot carry tombstone timestamps");
        Check.Throws<ArgumentException>(
            () => new CharacterLifecycleReceipt(
                CommandFamily.CharacterCreate,
                CharacterLifecycleReceiptStatus.Created,
                347,
                0,
                10,
                1,
                " invalid ",
                null,
                null,
                "1",
                Guid.NewGuid()),
            "successful lifecycle evidence requires a canonical name");
        Check.Throws<ArgumentException>(
            () => new CharacterLifecycleReceipt(
                CommandFamily.CharacterDelete,
                CharacterLifecycleReceiptStatus.CharacterNotFound,
                347,
                0,
                0,
                0,
                null!,
                null,
                null,
                "1",
                null),
            "lifecycle evidence rejects a null character name");
        Check.Throws<ArgumentException>(
            () => Receipt(
                CommandFamily.CharacterDelete,
                CharacterLifecycleReceiptStatus.StaleLifecycleVersion,
                restoreUntil,
                purgeAfter),
            "delete stale-version evidence cannot describe a tombstone");

        _ = Receipt(
            CommandFamily.CharacterDelete,
            CharacterLifecycleReceiptStatus.StaleLifecycleVersion);
        _ = Receipt(
            CommandFamily.CharacterDelete,
            CharacterLifecycleReceiptStatus.CharacterInUse);
        _ = Receipt(
            CommandFamily.CharacterRestore,
            CharacterLifecycleReceiptStatus.StaleLifecycleVersion,
            restoreUntil,
            purgeAfter);

        var successfulCreate = Receipt(
            CommandFamily.CharacterCreate,
            CharacterLifecycleReceiptStatus.Created,
            outboxEventId: Guid.NewGuid());
        var rejectedCreate = Receipt(
            CommandFamily.CharacterCreate,
            CharacterLifecycleReceiptStatus.SlotOccupied);
        Check.Throws<ArgumentException>(
            () => CharacterLifecycleExecutionResult.Committed(
                rejectedCreate),
            "committed execution requires a successful receipt");
        Check.Throws<ArgumentException>(
            () => CharacterLifecycleExecutionResult.TerminalRejected(
                successfulCreate),
            "terminal rejection requires a failed receipt");
        _ = CharacterLifecycleExecutionResult.Duplicate(
            successfulCreate);
        _ = CharacterLifecycleExecutionResult.Duplicate(
            rejectedCreate);

        Check.Throws<InvalidDataException>(
            () => CharacterLifecyclePersistenceCodec.Decode(
                InvalidReceiptPayload(
                    CommandFamily.CharacterDelete,
                    CharacterLifecycleReceiptStatus.Created,
                    null,
                    null,
                    Guid.NewGuid())),
            "stored evidence rejects a cross-family success");
        Check.Throws<InvalidDataException>(
            () => CharacterLifecyclePersistenceCodec.Decode(
                InvalidReceiptPayload(
                    CommandFamily.CharacterDelete,
                    CharacterLifecycleReceiptStatus.Deleted,
                    restoreUntil,
                    null,
                    Guid.NewGuid())),
            "stored evidence rejects one-sided retention timestamps");
    }

    private static CharacterLifecycleReceipt Receipt(
        CommandFamily family,
        CharacterLifecycleReceiptStatus status,
        DateTimeOffset? restoreUntil = null,
        DateTimeOffset? purgeAfter = null,
        Guid? outboxEventId = null) =>
        new(
            family,
            status,
            347,
            0,
            10,
            1,
            "LifecycleHero",
            restoreUntil,
            purgeAfter,
            "1",
            outboxEventId);

    private static byte[] InvalidReceiptPayload(
        CommandFamily family,
        CharacterLifecycleReceiptStatus status,
        DateTimeOffset? restoreUntil,
        DateTimeOffset? purgeAfter,
        Guid? outboxEventId) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                contractVersion = 1,
                family = (ushort)family,
                status = (byte)status,
                accountId = 347,
                characterSlot = 0,
                characterId = 10,
                lifecycleVersion = 1,
                characterName = "LifecycleHero",
                restoreUntil,
                purgeAfter,
                auditReference = "1",
                outboxEventId
            });
}
