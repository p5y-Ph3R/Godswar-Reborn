using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodePetAppearanceChange(
        PetDurableReceipt receipt)
    {
        if (receipt.Status == PetDurableReceiptStatus.PetAppearanceChanged &&
            receipt.AppearanceChange is not { IsValid: true })
        {
            throw new InvalidDataException(
                "A successful appearance-change receipt requires evidence.");
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new PersistedPetAppearanceChangeReceipt(
                ContractVersion,
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
                receipt.AppearanceChange));
    }

    private static PetDurableReceipt DecodePetAppearanceChange(
        ReadOnlySpan<byte> payload)
    {
        var stored = JsonSerializer.Deserialize<
            PersistedPetAppearanceChangeReceipt>(payload) ??
            throw new InvalidDataException(
                "The pet appearance-change receipt is malformed.");
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
            AppearanceChange: stored.AppearanceChange);
    }

    private sealed record PersistedPetAppearanceChangeReceipt(
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
        PetAppearanceChangeEvidence? AppearanceChange);
}
