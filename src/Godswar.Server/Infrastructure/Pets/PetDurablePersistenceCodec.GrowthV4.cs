using System.Text.Json;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    private static byte[] EncodePetGrowthV4(PetDurableReceipt receipt)
    {
        var preview = receipt.GrowthPreview;
        return JsonSerializer.SerializeToUtf8Bytes(
            new PersistedPetGrowthReceiptV4(
                PreviousPetGrowthResetContractVersion,
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
                    : new PersistedPetGrowthPreviewV4(
                        preview.PreviewOperationId,
                        preview.PetId,
                        preview.PetLevel,
                        preview.ExpectedPetRevision,
                        preview.GrowthRates,
                        preview.ExpiresAtUtc,
                        preview.CurrentGrowthRates)));
    }

    private sealed record PersistedPetGrowthReceiptV4(
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
        PersistedPetGrowthPreviewV4? GrowthPreview);

    private sealed record PersistedPetGrowthPreviewV4(
        Guid PreviewOperationId,
        long PetId,
        short PetLevel,
        long ExpectedPetRevision,
        PetContentStatVector GrowthRates,
        DateTimeOffset ExpiresAtUtc,
        PetContentStatVector? CurrentGrowthRates)
    {
        public bool IsValid =>
            PreviewOperationId != Guid.Empty &&
            PetId > 0 &&
            PetLevel is >= 1 and <= 120 &&
            ExpectedPetRevision >= 0 &&
            ExpiresAtUtc > DateTimeOffset.UnixEpoch &&
            GrowthRates.Agility > 0 &&
            GrowthRates.Strength > 0 &&
            GrowthRates.Accuracy > 0 &&
            GrowthRates.Technique > 0 &&
            GrowthRates.Wisdom > 0 &&
            GrowthRates.Luck > 0;

        public bool HasAuthoritativeCurrentRates =>
            CurrentGrowthRates is { } current &&
            current.Agility > 0 &&
            current.Strength > 0 &&
            current.Accuracy > 0 &&
            current.Technique > 0 &&
            current.Wisdom > 0 &&
            current.Luck > 0;
    }
}
