using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static void ValidatePetRebirthForEncode(
        PetDurableReceipt receipt)
    {
        if (receipt.Family == CommandFamily.PetRebirth &&
            receipt.Status == PetDurableReceiptStatus.PetReborn &&
            receipt.RebirthGrowth is not { IsValid: true })
        {
            throw new InvalidDataException(
                "A new Rebirth receipt requires its exact Growth roll.");
        }
    }

    private static byte[] EncodePetRebirth(PetDurableReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PersistedPetRebirthReceipt(
                PetRebirthContractVersion,
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
                receipt.RebirthGrowth));

    private static void ValidateDecodedPetRebirth(
        short contractVersion,
        PetDurableReceipt receipt)
    {
        if (contractVersion == PetRebirthContractVersion &&
            receipt.Status == PetDurableReceiptStatus.PetReborn &&
            receipt.RebirthGrowth is not { IsValid: true })
        {
            throw new InvalidDataException(
                "The Rebirth receipt has no valid Growth roll.");
        }
    }

    private static PetDurableReceipt DecodePetRebirth(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedPetRebirthReceipt>(payload) ??
            throw new InvalidDataException(
                "The pet Rebirth durable receipt is malformed.");
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
            RebirthGrowth: stored.RebirthGrowth);
    }

    private sealed record PersistedPetRebirthReceipt(
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
        PetRebirthGrowthEvidence? RebirthGrowth);
}
