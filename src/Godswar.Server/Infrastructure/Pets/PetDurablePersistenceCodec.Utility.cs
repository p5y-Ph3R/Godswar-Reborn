using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodePetManagerUtility(
        PetDurableReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PersistedPetManagerUtilityReceipt(
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
                receipt.PetManagerUtility));

    private static PetDurableReceipt DecodePetManagerUtility(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedPetManagerUtilityReceipt>(
                payload) ?? throw new InvalidDataException(
                    "The Pet Manager utility receipt is malformed.");
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
            PetManagerUtility: stored.Utility);
    }

    private sealed record PersistedPetManagerUtilityReceipt(
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
        PetManagerUtilityEvidence? Utility);
}
