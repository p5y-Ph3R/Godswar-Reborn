using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    private static void CheckGrowthPreviewReceiptRoundTrip()
    {
        var operationId = Guid.NewGuid();
        var rolled = new PetContentStatVector(
            1.01m, 1.02m, 1.03m, 1.04m, 1.05m, 1.06m);
        var current = new PetContentStatVector(
            2.01m, 2.02m, 2.03m, 2.04m, 2.05m, 2.06m);
        var modifiers = new PetContentStatVector(
            .50m, .60m, .70m, .80m, .90m, 1.00m);
        var receipt = GrowthPreviewReceipt(
            new PetGrowthPreviewSnapshot(
                operationId,
                71,
                20,
                9,
                rolled,
                DateTimeOffset.UtcNow.AddMinutes(2),
                current,
                PetGrowthPreviewRateSemantics
                    .NatureBaseWithRebirthModifier,
                CompletedRebirths: 5,
                RebirthModifiers: modifiers));
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));

        Check.True(
            PetDurablePersistenceCodec.ReadContractVersion(payload) == 5 &&
            decoded.GrowthPreview is
                {
                    HasAuthoritativeCurrentRates: true,
                    UsesRebirthCountWidenedRates: true,
                    CompletedRebirths: 5
                } preview &&
            preview.PreviewOperationId == operationId &&
            preview.CurrentGrowthRates == current &&
            preview.GrowthRates == rolled &&
            preview.RebirthModifiers == modifiers,
            "Growth preview receipt persists nature, current, and Rebirth vectors");

        CheckLegacyV4GrowthPreviewReceipt(operationId, rolled, current);
        CheckLegacyV3GrowthPreviewReceipt(operationId, rolled);
    }

    private static void CheckLegacyV4GrowthPreviewReceipt(
        Guid operationId,
        PetContentStatVector rolled,
        PetContentStatVector current)
    {
        var preview = new PetGrowthPreviewSnapshot(
            operationId,
            71,
            20,
            9,
            rolled,
            DateTimeOffset.UtcNow.AddMinutes(2),
            current);
        var receipt = GrowthPreviewReceipt(preview);
        var payload = BuildLegacyV4GrowthPayload(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));
        Check.True(
            decoded.GrowthPreview is
                {
                    IsValid: true,
                    HasAuthoritativeCurrentRates: true,
                    UsesRebirthCountWidenedRates: false
                },
            "legacy v4 Growth preview preserves acceleration semantics");
    }

    private static byte[] BuildLegacyV4GrowthPayload(
        PetDurableReceipt receipt)
    {
        var preview = receipt.GrowthPreview ??
            throw new InvalidDataException("Legacy v4 preview is missing.");
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            ContractVersion = (short)4,
            Family = (ushort)receipt.Family,
            Status = (byte)receipt.Status,
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
            GrowthPreview = new
            {
                preview.PreviewOperationId,
                preview.PetId,
                preview.PetLevel,
                preview.ExpectedPetRevision,
                preview.GrowthRates,
                preview.ExpiresAtUtc,
                preview.CurrentGrowthRates,
                IsValid = true,
                HasAuthoritativeCurrentRates = true
            }
        });
    }

    private static void CheckLegacyV3GrowthPreviewReceipt(
        Guid operationId,
        PetContentStatVector rolled)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ContractVersion = (short)3,
            Family = (ushort)CommandFamily.PetGrowthReset,
            Status = (byte)PetDurableReceiptStatus.PetGrowthPreviewed,
            AccountId = 13,
            CharacterId = 2,
            KitBagSlot = 7,
            EquipmentSlot = -1,
            PetId = 71L,
            PetLevel = (short)20,
            PetExperience = 12_345L,
            PetRevision = 9L,
            IsCarried = true,
            IsSummoned = true,
            PresenceOperation = (byte)0,
            AggregateRevision = 5L,
            AuditReference = "legacy-v3-growth-preview",
            OutboxEventId = (Guid?)Guid.NewGuid(),
            GrowthPreview = new
            {
                PreviewOperationId = operationId,
                PetId = 71L,
                PetLevel = (short)20,
                ExpectedPetRevision = 9L,
                GrowthRates = rolled,
                ExpiresAtUtc = expiresAtUtc
            }
        });
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));

        Check.True(
            decoded.GrowthPreview is
                { IsValid: true, HasAuthoritativeCurrentRates: false },
            "legacy v3 Growth preview replays safely without inventing current rates");
    }

    private static PetDurableReceipt GrowthPreviewReceipt(
        PetGrowthPreviewSnapshot preview) =>
        new(
            CommandFamily.PetGrowthReset,
            PetDurableReceiptStatus.PetGrowthPreviewed,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 7,
            EquipmentSlot: -1,
            PetId: preview.PetId,
            PetLevel: preview.PetLevel,
            PetExperience: 12_345,
            PetRevision: preview.ExpectedPetRevision,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 5,
            AuditReference: "growth-preview-v4",
            OutboxEventId: Guid.NewGuid(),
            GrowthPreview: preview);
}
