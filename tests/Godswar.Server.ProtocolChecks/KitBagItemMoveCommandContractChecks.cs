using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class KitBagItemMoveCommandContractChecks
{
    public static Task RunAsync()
    {
        var subject = new CommandSubject(7, 13);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var operationId = Guid.NewGuid();
        var source = Item(4212, 2).ToCompactString();
        var destination = Item(5201, 1).ToCompactString();
        Check.True(
            KitBagItemMoveCommandEnvelope.TryCreateCommand(
                operationId,
                0,
                95,
                source,
                destination,
                out var command),
            "bounded distinct move slots are accepted");
        var envelope = KitBagItemMoveCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            14,
            (int)envelope.Family,
            "kit-bag move uses command family 14");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)KitBagItemMoveCommandEnvelope.Validate(envelope),
            "canonical item-move envelope validates");
        Check.True(
            string.Equals(
                envelope.OperationId,
                KitBagItemMoveCommandEnvelope.CreateOperationId(
                    subject,
                    operationId),
                StringComparison.Ordinal),
            "item-move operation identity is reproducible");

        CheckBounds(source);
        CheckTransport(subject, command);
        CheckIdentity(subject, connection, envelope);
        CheckReceipts(source, destination);
        return Task.CompletedTask;
    }

    private static void CheckBounds(string item)
    {
        Check.True(
            !KitBagItemMoveCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                4,
                4,
                item,
                "[]",
                out _),
            "same-slot secure movement is rejected");
        Check.True(
            !KitBagItemMoveCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                4,
                5,
                item,
                "[]",
                out _),
            "empty move UUID is rejected");
        Check.True(
            !KitBagItemMoveCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                -1,
                5,
                item,
                "[]",
                out _),
            "negative source slot is rejected");
        Check.True(
            !KitBagItemMoveCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                4,
                96,
                item,
                "[]",
                out _),
            "destination above 95 is rejected");
        var oversized = "[" + new string(
            '1',
            KitBagItemMoveCommandEnvelope
                .MaximumExpectedStateUtf8Bytes) + "]";
        Check.True(
            !KitBagItemMoveCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                4,
                5,
                oversized,
                "[]",
                out _),
            "each expected state is independently bounded");
        Check.True(
            !KitBagItemMoveCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                4,
                5,
                item,
                "[1]\n",
                out _),
            "control characters are rejected");
    }

    private static void CheckTransport(
        CommandSubject subject,
        KitBagItemMoveCommand command)
    {
        Check.Throws<ArgumentException>(
            () => KitBagItemMoveCommandEnvelope.Create(
                subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.LegacyTcp),
                DateTimeOffset.UtcNow,
                command),
            "legacy TCP cannot claim durable item-move identity");
        var secure = KitBagItemMoveCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureCommand),
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)KitBagItemMoveCommandEnvelope.Validate(secure),
            "secure-command provenance is accepted");
    }

    private static void CheckIdentity(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        CommandEnvelope<KitBagItemMoveCommand> original)
    {
        var changedSource = KitBagItemMoveCommandEnvelope.Create(
            subject,
            connection,
            original.ReceivedAt,
            original.Command with
            {
                ExpectedSourceCompactItemState = "[]"
            });
        var reversedRoles = KitBagItemMoveCommandEnvelope.Create(
            subject,
            connection,
            original.ReceivedAt,
            original.Command with
            {
                SourceKitBagSlot =
                    original.Command.DestinationKitBagSlot,
                DestinationKitBagSlot =
                    original.Command.SourceKitBagSlot,
                ExpectedSourceCompactItemState =
                    original.Command
                        .ExpectedDestinationCompactItemState,
                ExpectedDestinationCompactItemState =
                    original.Command.ExpectedSourceCompactItemState
            });
        Check.True(
            original.OperationId == changedSource.OperationId &&
            original.RequestHash != changedSource.RequestHash,
            "full source state is covered by the request digest");
        Check.True(
            original.OperationId == reversedRoles.OperationId &&
            original.RequestHash != reversedRoles.RequestHash,
            "ordered slots and role-tagged states cannot alias");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)KitBagItemMoveCommandEnvelope.Validate(
                original with { Command = changedSource.Command }),
            "tampered move state fails request validation");
    }

    private static void CheckReceipts(
        string source,
        string destination)
    {
        var moved = Receipt(
            KitBagItemMoveResultStatus.Moved,
            source,
            "[]",
            source,
            "[]",
            Guid.NewGuid());
        AssertRoundTrip(moved, "moved receipt round-trips");
        Check.True(
            KitBagItemMoveExecutionResult.Committed(moved).IsSuccess,
            "committed movement succeeds");

        var swapped = Receipt(
            KitBagItemMoveResultStatus.Swapped,
            source,
            destination,
            source,
            destination,
            Guid.NewGuid());
        AssertRoundTrip(swapped, "swap receipt round-trips");
        var empty = Receipt(
            KitBagItemMoveResultStatus.EmptySource,
            "[]",
            destination,
            "[]",
            "[]",
            null);
        AssertRoundTrip(empty, "empty-source receipt round-trips");
        var staleSource = Receipt(
            KitBagItemMoveResultStatus.StaleSource,
            source,
            "[]",
            destination,
            "[]",
            null);
        AssertRoundTrip(
            staleSource,
            "stale-source receipt round-trips");
        var staleDestination = Receipt(
            KitBagItemMoveResultStatus.StaleDestination,
            source,
            "[]",
            source,
            destination,
            null);
        AssertRoundTrip(
            staleDestination,
            "stale-destination receipt round-trips");
        Check.Throws<ArgumentException>(
            () => Receipt(
                KitBagItemMoveResultStatus.Swapped,
                source,
                destination,
                source,
                destination,
                null),
            "committed swap requires an outbox event");
        Check.Throws<ArgumentException>(
            () => Receipt(
                KitBagItemMoveResultStatus.StaleSource,
                source,
                "[]",
                source,
                "[]",
                null),
            "stale source cannot claim matching source state");
    }

    private static KitBagItemMoveExecutionReceipt Receipt(
        KitBagItemMoveResultStatus status,
        string expectedSource,
        string expectedDestination,
        string authoritativeSource,
        string authoritativeDestination,
        Guid? eventId) =>
        new(
            13,
            4,
            5,
            status,
            expectedSource,
            expectedDestination,
            authoritativeSource,
            authoritativeDestination,
            9,
            "audit:1",
            eventId);

    private static void AssertRoundTrip(
        KitBagItemMoveExecutionReceipt expected,
        string description)
    {
        var payload = KitBagItemMovePersistenceCodec.Encode(expected);
        var decoded =
            KitBagItemMovePersistenceCodec.DecodeAndVerify(
                System.Text.Encoding.UTF8.GetString(payload),
                KitBagItemMovePersistenceCodec.Hash(payload));
        Check.True(decoded == expected, description);
    }

    private static CompactItemEntry Item(uint id, short stack) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,1,1,0,{stack},0,0,,,,,,0,,,,,,,,,,,,]");
}
