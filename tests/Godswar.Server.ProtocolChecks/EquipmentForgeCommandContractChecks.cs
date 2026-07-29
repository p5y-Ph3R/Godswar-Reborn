using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class EquipmentForgeCommandContractChecks
{
    public static Task RunAsync()
    {
        var subject = new CommandSubject(7, 13);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var operationId = Guid.NewGuid();
        var equipment = Item(1000, stack: 1);
        var primary = Item(4212, stack: 2);
        var odds = Enumerable.Range(2, 25)
            .Select(slot => Selection(
                EquipmentForgeCommandItemRole.OddsMaterial,
                slot,
                quantity: 1,
                Item(4232, stack: 1)))
            .Reverse()
            .ToArray();

        Check.True(
            EquipmentForgeCommandEnvelope.TryCreateCommand(
                operationId,
                Selection(
                    EquipmentForgeCommandItemRole.Equipment,
                    0,
                    1,
                    equipment),
                Selection(
                    EquipmentForgeCommandItemRole.PrimaryMaterial,
                    1,
                    1,
                    primary),
                odds,
                out var command),
            "maximum multistack forge command is bounded");
        Check.True(
            command.OddsMaterials
                .Select(static item => item.KitBagSlot)
                .SequenceEqual(Enumerable.Range(2, 25)),
            "odds selections canonicalize by authoritative slot");
        var envelope = EquipmentForgeCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            (int)CommandFamily.EquipmentForge,
            (int)envelope.Family,
            "forge uses command family 3");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)EquipmentForgeCommandEnvelope.Validate(envelope),
            "forge envelope validates");
        Check.True(
            string.Equals(
                envelope.OperationId,
                EquipmentForgeCommandEnvelope.CreateOperationId(
                    subject,
                    operationId),
                StringComparison.Ordinal),
            "forge operation UUID identity is reproducible");

        CheckBounds(equipment, primary);
        CheckTransport(subject, command);
        CheckReceiptAndCodec(equipment);
        return Task.CompletedTask;
    }

    private static void CheckBounds(
        CompactItemEntry equipment,
        CompactItemEntry primary)
    {
        var validEquipment = Selection(
            EquipmentForgeCommandItemRole.Equipment,
            0,
            1,
            equipment);
        var validPrimary = Selection(
            EquipmentForgeCommandItemRole.PrimaryMaterial,
            1,
            1,
            primary);
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                validEquipment,
                validPrimary,
                [],
                out _),
            "empty forge UUID is rejected");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment with { KitBagSlot = -1 },
                validPrimary,
                [],
                out _),
            "negative forge slot is rejected");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment,
                validPrimary with { KitBagSlot = 96 },
                [],
                out _),
            "forge slot above 95 is rejected");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment,
                validPrimary with { KitBagSlot = 0 },
                [],
                out _),
            "duplicate forge slots are rejected");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment with { Quantity = 2 },
                validPrimary,
                [],
                out _),
            "equipment quantity must be one");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment,
                validPrimary,
                [
                    Selection(
                        EquipmentForgeCommandItemRole.OddsMaterial,
                        2,
                        25,
                        Item(4232, 25)),
                    Selection(
                        EquipmentForgeCommandItemRole.OddsMaterial,
                        3,
                        1,
                        Item(4232, 1))
                ],
                out _),
            "aggregate odds quantity above 25 is rejected");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment,
                validPrimary,
                [
                    Selection(
                        EquipmentForgeCommandItemRole.PrimaryMaterial,
                        2,
                        1,
                        Item(4232, 1))
                ],
                out _),
            "odds role spoofing is rejected");
        Check.True(
            !EquipmentForgeCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                validEquipment with
                {
                    ExpectedCompactItemState =
                        new string('x', 513)
                },
                validPrimary,
                [],
                out _),
            "oversized expected item state is rejected");
    }

    private static void CheckTransport(
        CommandSubject subject,
        EquipmentForgeCommand command)
    {
        Check.Throws<ArgumentException>(
            () => EquipmentForgeCommandEnvelope.Create(
                subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.LegacyTcp),
                DateTimeOffset.UtcNow,
                command),
            "legacy TCP cannot claim durable forge identity");
    }

    private static void CheckReceiptAndCodec(
        CompactItemEntry equipment)
    {
        var eventId = Guid.NewGuid();
        var succeeded = new EquipmentForgeExecutionReceipt(
            13,
            EquipmentForgeCommandResultStatus.Succeeded,
            materialType: 2,
            roll: 10,
            successProbability: 50,
            silverSpent: 1,
            equipment.ToCompactString(),
            (equipment with { Quality = 2 }).ToCompactString(),
            [
                new EquipmentForgeReceiptMaterial(
                    EquipmentForgeCommandItemRole.PrimaryMaterial,
                    1,
                    4212,
                    1,
                    2,
                    1)
            ],
            walletRevision: 9,
            inventoryRevision: 12,
            auditReference: "1",
            eventId);
        var payload = EquipmentForgePersistenceCodec.Encode(succeeded);
        var decoded = EquipmentForgePersistenceCodec.Decode(payload);
        Check.True(
            decoded.CharacterId == succeeded.CharacterId &&
            decoded.Status == succeeded.Status &&
            decoded.Materials.SequenceEqual(succeeded.Materials) &&
            decoded.OutboxEventId == succeeded.OutboxEventId &&
            decoded.Roll == 10 &&
            decoded.Probability == 50 &&
            CompactItemEntry.Parse(
                decoded.EquipmentBeforeCompactItemState) == equipment &&
            CompactItemEntry.Parse(
                decoded.EquipmentAfterCompactItemState) ==
                equipment with { Quality = 2 },
            "successful forge receipt round-trips exact roll and equipment");

        var failed = new EquipmentForgeExecutionReceipt(
            13,
            EquipmentForgeCommandResultStatus.FailedRoll,
            materialType: 2,
            roll: 99,
            successProbability: 50,
            silverSpent: 1,
            equipment.ToCompactString(),
            equipment.ToCompactString(),
            [
                new EquipmentForgeReceiptMaterial(
                    EquipmentForgeCommandItemRole.PrimaryMaterial,
                    1,
                    4212,
                    1,
                    1,
                    0)
            ],
            walletRevision: 10,
            inventoryRevision: 13,
            auditReference: "contract:failed",
            Guid.NewGuid());
        var decodedFailed = EquipmentForgePersistenceCodec.Decode(
            EquipmentForgePersistenceCodec.Encode(failed));
        Check.True(
            decodedFailed.Status ==
                EquipmentForgeCommandResultStatus.FailedRoll &&
            decodedFailed.Roll == 99 &&
            string.Equals(
                decodedFailed.EquipmentBeforeCompactItemState,
                decodedFailed.EquipmentAfterCompactItemState,
                StringComparison.Ordinal) &&
            decodedFailed.Materials.SequenceEqual(failed.Materials),
            "failed roll receipt preserves exact sampled roll");

        var rejectionAfterPriorActivity =
            new EquipmentForgeExecutionReceipt(
                13,
                EquipmentForgeCommandResultStatus.StaleSelection,
                materialType: 0,
                roll: -1,
                successProbability: 0,
                silverSpent: 0,
                equipmentBeforeCompactItemState: string.Empty,
                equipmentAfterCompactItemState: string.Empty,
                materials: [],
                walletRevision: 8,
                inventoryRevision: 11,
                auditReference: "contract:rejected",
                outboxEventId: null);
        Check.True(
            rejectionAfterPriorActivity.InventoryRevision == 11 &&
            rejectionAfterPriorActivity.OutboxEventId is null,
            "terminal rejection retains pre-existing revisions");

        Check.Throws<ArgumentException>(
            () => new EquipmentForgeExecutionReceipt(
                13,
                EquipmentForgeCommandResultStatus.Succeeded,
                2,
                0,
                100,
                1,
                equipment.ToCompactString(),
                equipment.ToCompactString(),
                succeeded.Materials,
                1,
                1,
                "invalid:success",
                Guid.NewGuid()),
            "success cannot carry unchanged equipment");
        Check.Throws<ArgumentException>(
            () => new EquipmentForgeExecutionReceipt(
                13,
                EquipmentForgeCommandResultStatus.FailedRoll,
                2,
                99,
                50,
                1,
                equipment.ToCompactString(),
                (equipment with { Quality = 2 }).ToCompactString(),
                failed.Materials,
                1,
                1,
                "invalid:failed",
                Guid.NewGuid()),
            "failed roll cannot carry changed equipment");

        var hash = EquipmentForgePersistenceCodec.Hash(payload);
        hash[0] ^= 0xFF;
        Check.Throws<InvalidDataException>(
            () => EquipmentForgePersistenceCodec.DecodeAndVerify(
                Encoding.UTF8.GetString(payload),
                hash,
                EquipmentForgePersistenceCodec.CommittedResultCode,
                expectedAuditId: 1),
            "stored forge hash tampering is rejected");
    }

    private static EquipmentForgeCommandSelection Selection(
        EquipmentForgeCommandItemRole role,
        int slot,
        int quantity,
        CompactItemEntry item) =>
        new(role, slot, quantity, item.ToCompactString());

    private static CompactItemEntry Item(
        uint id,
        short stack) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,1,1,0,{stack},0,0,,,,,,0,,,,,,,,,,,,]");
}
