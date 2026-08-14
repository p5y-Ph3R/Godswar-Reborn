using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    private const string HatchRankContentRevision =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static void CheckHatchRankReceiptRoundTrip()
    {
        var evidence = new PetHatchRankEvidence(
            Rank: 1.50m,
            OutcomeOrder: 2,
            Roll: 97,
            ContentRevision: HatchRankContentRevision);
        var receipt = new PetDurableReceipt(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.EggHatched,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 31,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: true,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "hatch-rank-v2",
            OutboxEventId: Guid.NewGuid(),
            HatchRank: evidence);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));

        Check.True(
            PetDurablePersistenceCodec.ContractVersionFor(
                CommandFamily.BagItemActivation) ==
                PetDurablePersistenceCodec.BagItemActivationContractVersion &&
            decoded == receipt &&
            decoded.HatchRank == evidence,
            "rank-aware hatch receipt has a canonical v2 round trip");
        var consumer = new PetDurableOutboxConsumer();
        var message = CreateOutboxMessage(receipt, payload);
        consumer.ConsumeAsync(message).AsTask().GetAwaiter().GetResult();
        Check.Throws<InvalidDataException>(
            () => consumer.ConsumeAsync(
                    CreateOutboxMessage(
                        receipt,
                        payload,
                        PetDurablePersistenceCodec.ContractVersion))
                .AsTask().GetAwaiter().GetResult(),
            "pet outbox rejects a schema version that disagrees with its payload");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                receipt with { HatchRank = null }),
            "new hatch receipts cannot discard their rank evidence");
        Check.Throws<InvalidDataException>(
            () => (receipt with
            {
                HatchRank = evidence with { ContentRevision = null! }
            }).Validate(),
            "malformed hatch revision evidence fails closed");
        Check.Throws<InvalidDataException>(
            () => (receipt with
            {
                Status = PetDurableReceiptStatus.EquipmentEquipped,
                EquipmentSlot = 1
            }).Validate(),
            "hatch evidence cannot be attached to another bag operation");

        CheckLegacyHatchReceipt();
    }

    private static OutboxEventMessage CreateOutboxMessage(
        PetDurableReceipt receipt,
        byte[] payload,
        int? schemaVersion = null) =>
        new(
            receipt.OutboxEventId ?? throw new InvalidDataException(
                "Hatch receipt has no outbox event ID."),
            PetDurablePersistenceCodec.ConsumerKey,
            PetDurablePersistenceCodec.AggregateType,
            PetDurablePersistenceCodec.AggregateKey(receipt.CharacterId),
            receipt.AggregateRevision,
            PetDurablePersistenceCodec.EventType(receipt.Family),
            schemaVersion ??
                PetDurablePersistenceCodec.ContractVersionFor(
                    receipt.Family),
            DateTimeOffset.UtcNow,
            payload);

    private static void CheckLegacyHatchReceipt()
    {
        var outboxEventId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ContractVersion = PetDurablePersistenceCodec.ContractVersion,
            Family = (ushort)CommandFamily.BagItemActivation,
            Status = (byte)PetDurableReceiptStatus.EggHatched,
            AccountId = 13,
            CharacterId = 2,
            KitBagSlot = 31,
            EquipmentSlot = -1,
            PetId = 70L,
            PetLevel = (short)1,
            PetExperience = 0L,
            PetRevision = 0L,
            IsCarried = true,
            IsSummoned = false,
            PresenceOperation = (byte)0,
            AggregateRevision = 1L,
            AuditReference = "legacy-hatch-v1",
            OutboxEventId = (Guid?)outboxEventId
        });
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));

        Check.True(
            decoded.Status == PetDurableReceiptStatus.EggHatched &&
            decoded.HatchRank is null &&
            decoded.OutboxEventId == outboxEventId,
            "legacy v1 hatch receipts remain replay-decodable without invented evidence");
    }
}
