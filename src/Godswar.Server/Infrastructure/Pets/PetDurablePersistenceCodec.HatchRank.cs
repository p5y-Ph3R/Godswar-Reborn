using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodeBagItemActivation(
        PetDurableReceipt receipt)
    {
        if (receipt.Status == PetDurableReceiptStatus.EggHatched &&
            receipt.HatchRank is null)
        {
            throw new InvalidDataException(
                "A new pet hatch receipt must retain rank evidence.");
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new PersistedBagItemActivationReceipt(
                BagItemActivationContractVersion,
                (ushort)receipt.Family,
                (byte)receipt.Status,
                receipt.AccountId,
                receipt.CharacterId,
                receipt.KitBagSlot,
                receipt.EquipmentSlot,
                receipt.PetId,
                receipt.PetLevel,
                receipt.PetExperience,
                receipt.PetRevision,
                receipt.IsCarried,
                receipt.IsSummoned,
                receipt.PresenceOperation,
                receipt.AggregateRevision,
                receipt.AuditReference,
                receipt.OutboxEventId,
                receipt.HatchRank,
                receipt.SkillLearn));
    }

    private static PetDurableReceipt DecodeBagItemActivation(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedBagItemActivationReceipt>(
                payload) ?? throw new InvalidDataException(
                "The pet bag-activation durable receipt is malformed.");
        if ((PetDurableReceiptStatus)stored.Status ==
                PetDurableReceiptStatus.EggHatched &&
            stored.HatchRank is null)
        {
            throw new InvalidDataException(
                "The pet hatch receipt omitted rank evidence.");
        }

        return new PetDurableReceipt(
            (CommandFamily)stored.Family,
            (PetDurableReceiptStatus)stored.Status,
            stored.AccountId,
            stored.CharacterId,
            stored.KitBagSlot,
            stored.EquipmentSlot,
            stored.PetId,
            stored.PetLevel,
            stored.PetExperience,
            stored.PetRevision,
            stored.IsCarried,
            stored.IsSummoned,
            stored.PresenceOperation,
            stored.AggregateRevision,
            stored.AuditReference,
            stored.OutboxEventId,
            HatchRank: stored.HatchRank,
            SkillLearn: stored.SkillLearn);
    }

    private static byte[] EncodeBagItemActivationV2(
        PetDurableReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PersistedBagItemActivationReceiptV2(
                PreviousBagItemActivationContractVersion,
                (ushort)receipt.Family,
                (byte)receipt.Status,
                receipt.AccountId,
                receipt.CharacterId,
                receipt.KitBagSlot,
                receipt.EquipmentSlot,
                receipt.PetId,
                receipt.PetLevel,
                receipt.PetExperience,
                receipt.PetRevision,
                receipt.IsCarried,
                receipt.IsSummoned,
                receipt.PresenceOperation,
                receipt.AggregateRevision,
                receipt.AuditReference,
                receipt.OutboxEventId,
                receipt.HatchRank));

    private static PetDurableReceipt DecodeBagItemActivationV2(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedBagItemActivationReceiptV2>(
                payload) ?? throw new InvalidDataException(
                "The v2 pet bag-activation receipt is malformed.");
        return new PetDurableReceipt(
            (CommandFamily)stored.Family,
            (PetDurableReceiptStatus)stored.Status,
            stored.AccountId,
            stored.CharacterId,
            stored.KitBagSlot,
            stored.EquipmentSlot,
            stored.PetId,
            stored.PetLevel,
            stored.PetExperience,
            stored.PetRevision,
            stored.IsCarried,
            stored.IsSummoned,
            stored.PresenceOperation,
            stored.AggregateRevision,
            stored.AuditReference,
            stored.OutboxEventId,
            HatchRank: stored.HatchRank);
    }

    private sealed record PersistedBagItemActivationReceipt(
        short ContractVersion,
        ushort Family,
        byte Status,
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
        Guid? OutboxEventId,
        PetHatchRankEvidence? HatchRank,
        PetSkillLearnEvidence? SkillLearn);

    private sealed record PersistedBagItemActivationReceiptV2(
        short ContractVersion,
        ushort Family,
        byte Status,
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
        Guid? OutboxEventId,
        PetHatchRankEvidence? HatchRank);
}
