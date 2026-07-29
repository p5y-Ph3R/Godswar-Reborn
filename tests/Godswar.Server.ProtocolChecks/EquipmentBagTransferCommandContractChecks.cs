using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class EquipmentBagTransferCommandContractChecks
{
    public static Task RunAsync()
    {
        var subject = new CommandSubject(7, 19);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var operationId = Guid.NewGuid();
        var equipment = Item(1_000).ToCompactString();
        var empty = CompactItemEntry.Empty.ToCompactString();
        Check.True(
            EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                operationId,
                EquipmentSlots.Weapon,
                95,
                equipment,
                empty,
                out var command),
            "bounded equipment/bag transfer is accepted");
        var envelope = EquipmentBagTransferCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            15,
            (int)envelope.Family,
            "equipment/bag transfer uses command family 15");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)EquipmentBagTransferCommandEnvelope.Validate(envelope),
            "canonical equipment/bag envelope validates");
        Check.True(
            string.Equals(
                envelope.OperationId,
                EquipmentBagTransferCommandEnvelope.CreateOperationId(
                    subject,
                    operationId),
                StringComparison.Ordinal),
            "equipment/bag operation identity is reproducible");

        CheckBounds(equipment, empty);
        CheckTransport(subject, command);
        CheckIdentity(subject, connection, envelope);
        CheckReceipts(equipment, empty);
        return Task.CompletedTask;
    }

    private static void CheckBounds(string equipment, string empty)
    {
        Check.True(
            !EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                EquipmentSlots.Weapon,
                1,
                equipment,
                empty,
                out _),
            "empty transfer UUID is rejected");
        Check.True(
            !EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                -1,
                1,
                equipment,
                empty,
                out _),
            "negative equipment slot is rejected");
        Check.True(
            !EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                EquipmentSlots.Mount + 1,
                1,
                equipment,
                empty,
                out _),
            "equipment slot above mount is rejected");
        Check.True(
            !EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                EquipmentSlots.Weapon,
                96,
                equipment,
                empty,
                out _),
            "bag slot above 95 is rejected");
        Check.True(
            !EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                EquipmentSlots.Weapon,
                1,
                equipment,
                empty,
                mountRuntimeBlocked: true,
                out _),
            "Ride runtime observation is valid only for the mount slot");
        var oversized = "[" + new string(
            '1',
            EquipmentBagTransferCommandEnvelope
                .MaximumExpectedStateUtf8Bytes) + "]";
        Check.True(
            !EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                EquipmentSlots.Weapon,
                1,
                oversized,
                empty,
                out _),
            "each expected transfer state is independently bounded");
    }

    private static void CheckTransport(
        CommandSubject subject,
        EquipmentBagTransferCommand command)
    {
        Check.Throws<ArgumentException>(
            () => EquipmentBagTransferCommandEnvelope.Create(
                subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.LegacyTcp),
                DateTimeOffset.UtcNow,
                command),
            "legacy TCP cannot claim durable transfer identity");
        var secure = EquipmentBagTransferCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureCommand),
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)EquipmentBagTransferCommandEnvelope.Validate(secure),
            "secure-command transfer provenance is accepted");
    }

    private static void CheckIdentity(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        CommandEnvelope<EquipmentBagTransferCommand> original)
    {
        var changedEquipment =
            EquipmentBagTransferCommandEnvelope.Create(
                subject,
                connection,
                original.ReceivedAt,
                original.Command with
                {
                    ExpectedEquipmentCompactItemState = "[]"
                });
        var reversedRoles =
            EquipmentBagTransferCommandEnvelope.Create(
                subject,
                connection,
                original.ReceivedAt,
                original.Command with
                {
                    ExpectedEquipmentCompactItemState =
                        original.Command.ExpectedKitBagCompactItemState,
                    ExpectedKitBagCompactItemState =
                        original.Command
                            .ExpectedEquipmentCompactItemState
                });
        var changedCoordinate =
            EquipmentBagTransferCommandEnvelope.Create(
                subject,
                connection,
                original.ReceivedAt,
                original.Command with { KitBagSlot = 94 });
        Check.True(
            EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                original.Command.ClientOperationId,
                EquipmentSlots.Mount,
                original.Command.KitBagSlot,
                original.Command.ExpectedEquipmentCompactItemState,
                original.Command.ExpectedKitBagCompactItemState,
                mountRuntimeBlocked: false,
                out var mountCommand),
            "unblocked mount transfer command is valid");
        var unblockedMount =
            EquipmentBagTransferCommandEnvelope.Create(
                subject,
                connection,
                original.ReceivedAt,
                mountCommand);
        var blockedMount =
            EquipmentBagTransferCommandEnvelope.Create(
                subject,
                connection,
                original.ReceivedAt,
                mountCommand with { MountRuntimeBlocked = true });
        Check.True(
            original.OperationId == changedEquipment.OperationId &&
            original.RequestHash != changedEquipment.RequestHash,
            "full equipment state is covered by transfer digest");
        Check.True(
            original.OperationId == reversedRoles.OperationId &&
            original.RequestHash != reversedRoles.RequestHash,
            "equipment and bag state roles cannot alias");
        Check.True(
            original.OperationId == changedCoordinate.OperationId &&
            original.RequestHash != changedCoordinate.RequestHash,
            "ordered transfer coordinates are request-hash bound");
        Check.True(
            unblockedMount.OperationId == blockedMount.OperationId &&
            unblockedMount.RequestHash != blockedMount.RequestHash,
            "server-observed Ride runtime block is request-hash bound");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)EquipmentBagTransferCommandEnvelope.Validate(
                original with
                {
                    Command = changedEquipment.Command
                }),
            "tampered transfer state fails request validation");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)EquipmentBagTransferCommandEnvelope.Validate(
                original with
                {
                    Command = changedCoordinate.Command
                }),
            "tampered transfer coordinate fails request validation");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)EquipmentBagTransferCommandEnvelope.Validate(
                unblockedMount with
                {
                    Command =
                        unblockedMount.Command with
                        {
                            MountRuntimeBlocked = true
                        }
                }),
            "tampered Ride runtime observation fails request validation");
    }

    private static void CheckReceipts(
        string equipment,
        string empty)
    {
        var unequipped = Receipt(
            EquipmentBagTransferResultStatus.Unequipped,
            equipment,
            empty,
            Guid.NewGuid());
        Check.True(
            EquipmentBagTransferExecutionResult
                .Committed(unequipped).IsSuccess,
            "committed unequip succeeds");
        var equipped = Receipt(
            EquipmentBagTransferResultStatus.Equipped,
            empty,
            equipment,
            Guid.NewGuid());
        Check.True(
            EquipmentBagTransferExecutionResult
                .Duplicate(equipped).IsSuccess,
            "replayed equip succeeds without another mutation");
        var occupied = Receipt(
            EquipmentBagTransferResultStatus.BothOccupied,
            equipment,
            equipment,
            null);
        Check.True(
            !EquipmentBagTransferExecutionResult
                .TerminalRejected(occupied).IsSuccess,
            "occupied pair is a durable rejection");
        Check.Throws<ArgumentException>(
            () => Receipt(
                EquipmentBagTransferResultStatus.Unequipped,
                equipment,
                empty,
                null),
            "committed unequip evidence requires an outbox event");
        Check.Throws<ArgumentException>(
            () => Receipt(
                EquipmentBagTransferResultStatus.BothEmpty,
                equipment,
                empty,
                null),
            "both-empty evidence must contain two empty states");
        Check.Throws<ArgumentException>(
            () => Receipt(
                EquipmentBagTransferResultStatus.ItemNotEquipment,
                equipment,
                empty,
                null),
            "equip-only rejection cannot claim unequip occupancy");
        var wrongSlotUnequip = Receipt(
            EquipmentBagTransferResultStatus.WrongEquipmentSlot,
            equipment,
            empty,
            null);
        Check.True(
            EquipmentBagTransferExecutionResult
                .TerminalRejected(wrongSlotUnequip).IsDurable,
            "wrong-slot rejection accepts occupied equipment source");
        var wrongSlotEquip = Receipt(
            EquipmentBagTransferResultStatus.WrongEquipmentSlot,
            empty,
            equipment,
            null);
        Check.True(
            EquipmentBagTransferExecutionResult
                .TerminalRejected(wrongSlotEquip).IsDurable,
            "wrong-slot rejection accepts occupied bag source");
        Check.Throws<ArgumentException>(
            () => Receipt(
                EquipmentBagTransferResultStatus.WrongEquipmentSlot,
                equipment,
                equipment,
                null),
            "wrong-slot rejection cannot claim two occupied sources");
        var rideBlocked = Receipt(
            EquipmentBagTransferResultStatus.RideRuntimeBlocked,
            equipment,
            empty,
            null,
            EquipmentSlots.Mount);
        Check.True(
            EquipmentBagTransferExecutionResult
                .TerminalRejected(rideBlocked).IsDurable,
            "Ride runtime rejection is durable for the mount slot");
        Check.Throws<ArgumentException>(
            () => Receipt(
                EquipmentBagTransferResultStatus.RideRuntimeBlocked,
                equipment,
                empty,
                null),
            "Ride runtime rejection cannot claim a non-mount slot");
    }

    private static EquipmentBagTransferExecutionReceipt Receipt(
        EquipmentBagTransferResultStatus status,
        string equipment,
        string kitBag,
        Guid? eventId,
        int equipmentSlot = EquipmentSlots.Weapon) =>
        new(
            19,
            equipmentSlot,
            25,
            status,
            equipment,
            kitBag,
            equipment,
            kitBag,
            eventId.HasValue ? 7 : 0,
            "audit:equipment-bag-transfer:contract",
            eventId);

    private static CompactItemEntry Item(uint id) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 1
        };
}
