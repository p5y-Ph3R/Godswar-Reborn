using System.Text.Json;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodePetGrowthV3(PetDurableReceipt receipt)
    {
        var preview = receipt.GrowthPreview;
        return JsonSerializer.SerializeToUtf8Bytes(
            new PersistedPetGrowthReceiptV3(
                LegacyPetGrowthResetContractVersion,
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
                preview is null
                    ? null
                    : new PersistedPetGrowthPreviewV3(
                        preview.PreviewOperationId,
                        preview.PetId,
                        preview.PetLevel,
                        preview.ExpectedPetRevision,
                        preview.GrowthRates,
                        preview.ExpiresAtUtc)));
    }

    private sealed record PersistedPetGrowthReceiptV3(
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
        PersistedPetGrowthPreviewV3? GrowthPreview);

    private sealed record PersistedPetGrowthPreviewV3(
        Guid PreviewOperationId,
        long PetId,
        short PetLevel,
        long ExpectedPetRevision,
        PetContentStatVector GrowthRates,
        DateTimeOffset ExpiresAtUtc);
}
