using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class KitBagItemDeleteCommandContractChecks
{
    public static Task RunAsync()
    {
        var subject = new CommandSubject(7, 13);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var clientOperationId = Guid.NewGuid();
        var itemState = Item(4212, 2).ToCompactString();

        Check.True(
            KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                clientOperationId,
                95,
                itemState,
                out var command),
            "maximum kit-bag slot is accepted");
        var envelope = KitBagItemDeleteCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            13,
            (int)envelope.Family,
            "kit-bag delete uses command family 13");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)envelope.IdentityStrength,
            "kit-bag delete requires a client operation UUID");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)KitBagItemDeleteCommandEnvelope.Validate(envelope),
            "canonical kit-bag delete envelope validates");
        Check.True(
            string.Equals(
                envelope.OperationId,
                KitBagItemDeleteCommandEnvelope.CreateOperationId(
                    subject,
                    clientOperationId),
                StringComparison.Ordinal),
            "kit-bag delete operation identity is reproducible");

        CheckBounds(itemState);
        CheckTransport(subject, command);
        CheckIdentity(subject, connection, command, envelope);
        CheckReceiptsAndCodec(itemState);
        return Task.CompletedTask;
    }

    private static void CheckBounds(string itemState)
    {
        Check.True(
            KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                0,
                "[]",
                out _),
            "an explicitly empty selected slot is canonical");
        Check.True(
            !KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                0,
                itemState,
                out _),
            "empty delete operation UUID is rejected");
        Check.True(
            !KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                -1,
                itemState,
                out _),
            "negative kit-bag slot is rejected");
        Check.True(
            !KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                96,
                itemState,
                out _),
            "kit-bag slot above 95 is rejected");
        Check.True(
            !KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                0,
                new string('x',
                    KitBagItemDeleteCommandEnvelope
                        .MaximumExpectedStateUtf8Bytes + 1),
                out _),
            "oversized expected item state is rejected");
        Check.True(
            !KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                0,
                "[1]\n",
                out _),
            "control characters are rejected");
    }

    private static void CheckTransport(
        CommandSubject subject,
        KitBagItemDeleteCommand command)
    {
        Check.Throws<ArgumentException>(
            () => KitBagItemDeleteCommandEnvelope.Create(
                subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.LegacyTcp),
                DateTimeOffset.UtcNow,
                command),
            "legacy TCP cannot claim durable item-delete identity");

        var secure = KitBagItemDeleteCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureCommand),
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)KitBagItemDeleteCommandEnvelope.Validate(secure),
            "secure-command provenance is accepted");
    }

    private static void CheckIdentity(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        KitBagItemDeleteCommand command,
        CommandEnvelope<KitBagItemDeleteCommand> envelope)
    {
        var differentState = command with
        {
            ExpectedCompactItemState = "[]"
        };
        var differentStateEnvelope =
            KitBagItemDeleteCommandEnvelope.Create(
                subject,
                connection,
                envelope.ReceivedAt,
                differentState);
        Check.True(
            string.Equals(
                envelope.OperationId,
                differentStateEnvelope.OperationId,
                StringComparison.Ordinal) &&
            !string.Equals(
                envelope.RequestHash,
                differentStateEnvelope.RequestHash,
                StringComparison.Ordinal),
            "same UUID with changed expected state is a hash conflict");

        var differentSlotEnvelope =
            KitBagItemDeleteCommandEnvelope.Create(
                subject,
                connection,
                envelope.ReceivedAt,
                command with { KitBagSlot = command.KitBagSlot - 1 });
        Check.True(
            string.Equals(
                envelope.OperationId,
                differentSlotEnvelope.OperationId,
                StringComparison.Ordinal) &&
            !string.Equals(
                envelope.RequestHash,
                differentSlotEnvelope.RequestHash,
                StringComparison.Ordinal),
            "same UUID with changed slot is a hash conflict");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)KitBagItemDeleteCommandEnvelope.Validate(
                envelope with
                {
                    Command = differentState
                }),
            "tampered command is rejected by request hash");
    }

    private static void CheckReceiptsAndCodec(string itemState)
    {
        var deleted = new KitBagItemDeleteExecutionReceipt(
            13,
            4,
            KitBagItemDeleteResultStatus.Deleted,
            itemState,
            itemState,
            9,
            "audit:1",
            Guid.NewGuid());
        AssertRoundTrip(deleted, "deleted receipt round-trips");
        Check.True(
            KitBagItemDeleteExecutionResult.Committed(deleted).IsSuccess,
            "deleted committed result is successful");

        var empty = new KitBagItemDeleteExecutionReceipt(
            13,
            5,
            KitBagItemDeleteResultStatus.EmptySlot,
            "[]",
            "[]",
            9,
            "audit:2",
            null);
        AssertRoundTrip(empty, "empty-slot receipt round-trips");
        Check.True(
            !KitBagItemDeleteExecutionResult
                .TerminalRejected(empty).IsSuccess,
            "empty slot is a durable terminal rejection");

        var stale = new KitBagItemDeleteExecutionReceipt(
            13,
            6,
            KitBagItemDeleteResultStatus.StaleSelection,
            itemState,
            "[]",
            9,
            "audit:3",
            null);
        AssertRoundTrip(stale, "stale-selection receipt round-trips");
        Check.Throws<ArgumentException>(
            () => new KitBagItemDeleteExecutionReceipt(
                13,
                6,
                KitBagItemDeleteResultStatus.StaleSelection,
                itemState,
                itemState,
                9,
                "audit:4",
                null),
            "stale receipt cannot claim equal states");
        Check.Throws<ArgumentException>(
            () => new KitBagItemDeleteExecutionReceipt(
                13,
                6,
                KitBagItemDeleteResultStatus.EmptySlot,
                "[]",
                "[]",
                9,
                "audit:5",
                Guid.NewGuid()),
            "terminal rejection cannot publish an outbox event");
    }

    private static void AssertRoundTrip(
        KitBagItemDeleteExecutionReceipt expected,
        string description)
    {
        var payload = KitBagItemDeletePersistenceCodec.Encode(expected);
        var decoded = KitBagItemDeletePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            KitBagItemDeletePersistenceCodec.Hash(payload));
        Check.True(
            decoded == expected,
            description);
    }

    private static CompactItemEntry Item(uint id, short stack) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,1,1,0,{stack},0,0,,,,,,0,,,,,,,,,,,,]");
}
