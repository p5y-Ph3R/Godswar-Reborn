using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using System.Text;
using System.Text.Json.Nodes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    private static void CheckRebirthGrowthReceiptRoundTrip()
    {
        var roll = new PetRebirthGrowthEvidence(
            new PetContentStatVector(
                0.10m, 0.12m, 0.14m, 0.16m, 0.18m, 0.20m));
        var receipt = new PetDurableReceipt(
            CommandFamily.PetRebirth,
            PetDurableReceiptStatus.PetReborn,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 7,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 1,
            PetExperience: 12_345,
            PetRevision: 9,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 5,
            AuditReference: "rebirth-growth",
            OutboxEventId: Guid.NewGuid(),
            RebirthGrowth: roll);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));

        Check.True(
            PetDurablePersistenceCodec.ContractVersionFor(
                CommandFamily.PetRebirth) ==
                PetDurablePersistenceCodec.PetRebirthContractVersion &&
            decoded == receipt &&
            decoded.RebirthGrowth?.ToOrderedIncrease()
                .SequenceEqual(roll.ToOrderedIncrease()) == true,
            "rebirth receipt durably replays the exact six-stat Growth roll");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                receipt with { RebirthGrowth = null }),
            "new committed rebirth receipt cannot omit its Growth roll");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                receipt with
                {
                    RebirthGrowth = new PetRebirthGrowthEvidence(
                        new PetContentStatVector(
                            0.21m, 0.12m, 0.14m,
                            0.16m, 0.18m, 0.20m))
                }),
            "rebirth receipt rejects a Growth roll outside native bounds");

        var missingV2Evidence = JsonNode.Parse(payload)?.AsObject() ??
            throw new InvalidOperationException(
                "The encoded rebirth receipt is not JSON.");
        missingV2Evidence[nameof(PetDurableReceipt.RebirthGrowth)] = null;
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Decode(
                Encoding.UTF8.GetBytes(missingV2Evidence.ToJsonString())),
            "rebirth v2 decode fails closed without its Growth roll");

        var legacyV1 = JsonNode.Parse(payload)?.AsObject() ??
            throw new InvalidOperationException(
                "The encoded rebirth receipt is not JSON.");
        legacyV1["ContractVersion"] =
            PetDurablePersistenceCodec.ContractVersion;
        legacyV1.Remove(nameof(PetDurableReceipt.RebirthGrowth));
        var legacyPayload = Encoding.UTF8.GetBytes(
            legacyV1.ToJsonString());
        var decodedLegacy = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(legacyPayload),
            PetDurablePersistenceCodec.Hash(legacyPayload));
        Check.True(
            decodedLegacy.Status == PetDurableReceiptStatus.PetReborn &&
            decodedLegacy.RebirthGrowth is null,
            "legacy rebirth v1 remains replay-decodable without roll evidence");
    }
}
