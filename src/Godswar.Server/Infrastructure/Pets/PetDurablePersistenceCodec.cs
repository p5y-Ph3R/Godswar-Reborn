using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    public const short ContractVersion = 1;
    private const short PreviousBagItemActivationContractVersion = 2;
    public const short BagItemActivationContractVersion = 3;
    public const short PetToPetMergeContractVersion = 2;
    private const short LegacyPetGrowthResetContractVersion = 3;
    private const short PreviousPetGrowthResetContractVersion = 4;
    public const short PetGrowthResetContractVersion = 5;
    public const short PetBasicSavvyResetContractVersion = 2;
    public const short PetRebirthContractVersion = 2;
    public const string PrincipalType = "account";
    public const string AggregateType = "character_pet_value";
    public const string ConsumerKey = "pet_durable_v1";
    public const string OrderingPolicy = "strict";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(int characterId) =>
        $"character:{characterId}";

    public static byte[] Encode(PetDurableReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        if (receipt.Family == CommandFamily.PetGrowthReset &&
            receipt.Status == PetDurableReceiptStatus.PetGrowthPreviewed &&
            receipt.GrowthPreview is not
                {
                    HasAuthoritativeCurrentRates: true,
                    UsesRebirthCountWidenedRates: true
                })
        {
            throw new InvalidDataException(
                "A new Growth preview receipt requires current rates.");
        }
        ValidatePetRebirthForEncode(receipt);
        var payload = receipt.Family switch
        {
            CommandFamily.BagItemActivation =>
                EncodeBagItemActivation(receipt),
            CommandFamily.PetToPetMerge =>
                JsonSerializer.SerializeToUtf8Bytes(
                new PersistedPetToPetMergeReceipt(
                    PetToPetMergeContractVersion,
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
                    receipt.DeputyPetId,
                    receipt.PetMergeDelta)),
            CommandFamily.PetGrowthReset =>
                JsonSerializer.SerializeToUtf8Bytes(
                new PersistedPetGrowthReceipt(
                    PetGrowthResetContractVersion,
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
                    receipt.GrowthPreview)),
            CommandFamily.PetBasicSavvyReset =>
                JsonSerializer.SerializeToUtf8Bytes(
                new PersistedPetBasicSavvyReceipt(
                    PetBasicSavvyResetContractVersion,
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
                    receipt.BasicSavvyPreview)),
            CommandFamily.PetRebirth =>
                EncodePetRebirth(receipt),
            CommandFamily.PetAppearanceChange =>
                EncodePetAppearanceChange(receipt),
            CommandFamily.PetSoulContract =>
                EncodePetSoulContract(receipt),
            CommandFamily.PetManagerUtility =>
                EncodePetManagerUtility(receipt),
            _ => JsonSerializer.SerializeToUtf8Bytes(
                new PersistedReceipt(
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
                receipt.OutboxEventId))
        };
        return payload.Length <= OutboxEventMessage.MaximumPayloadBytes
            ? payload
            : throw new InvalidDataException(
                "The pet durable receipt exceeds its payload bound.");
    }

    public static PetDurableReceipt Decode(ReadOnlySpan<byte> payload)
    {
        var header = ReadHeader(payload);
        if (!IsSupportedContractVersion(
                (CommandFamily)header.Family,
                header.ContractVersion))
        {
            throw new InvalidDataException(
                "The pet durable receipt version is unsupported.");
        }

        var receipt = ((CommandFamily)header.Family,
            header.ContractVersion) switch
        {
            (CommandFamily.BagItemActivation,
                BagItemActivationContractVersion) =>
                DecodeBagItemActivation(payload),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersion) =>
                DecodeBagItemActivationV2(payload),
            (CommandFamily.PetToPetMerge,
                PetToPetMergeContractVersion) =>
                DecodePetToPetMerge(payload),
            (CommandFamily.PetGrowthReset,
                PetGrowthResetContractVersion) =>
                DecodePetGrowthReset(payload),
            (CommandFamily.PetGrowthReset,
                PreviousPetGrowthResetContractVersion) =>
                DecodePetGrowthReset(payload),
            (CommandFamily.PetGrowthReset,
                LegacyPetGrowthResetContractVersion) =>
                DecodePetGrowthReset(payload),
            (CommandFamily.PetBasicSavvyReset,
                PetBasicSavvyResetContractVersion) =>
                DecodePetBasicSavvyReset(payload),
            (CommandFamily.PetRebirth, PetRebirthContractVersion) =>
                DecodePetRebirth(payload),
            (CommandFamily.PetAppearanceChange, ContractVersion) =>
                DecodePetAppearanceChange(payload),
            (CommandFamily.PetSoulContract, ContractVersion) =>
                DecodePetSoulContract(payload),
            (CommandFamily.PetManagerUtility, ContractVersion) =>
                DecodePetManagerUtility(payload),
            _ => DecodeV1(payload)
        };
        receipt.Validate();
        if ((CommandFamily)header.Family ==
                CommandFamily.PetGrowthReset &&
            header.ContractVersion == PetGrowthResetContractVersion &&
            receipt.Status == PetDurableReceiptStatus.PetGrowthPreviewed &&
            receipt.GrowthPreview is not
                {
                    HasAuthoritativeCurrentRates: true,
                    UsesRebirthCountWidenedRates: true
                })
        {
            throw new InvalidDataException(
                "The Growth preview receipt has no current rates.");
        }
        if ((CommandFamily)header.Family == CommandFamily.PetRebirth)
        {
            ValidateDecodedPetRebirth(
                header.ContractVersion,
                receipt);
        }
        return receipt;
    }

    public static short ReadContractVersion(ReadOnlySpan<byte> payload) =>
        ReadHeader(payload).ContractVersion;

    public static short ContractVersionFor(CommandFamily family) =>
        family switch
        {
            CommandFamily.BagItemActivation =>
                BagItemActivationContractVersion,
            CommandFamily.PetToPetMerge =>
                PetToPetMergeContractVersion,
            CommandFamily.PetGrowthReset =>
                PetGrowthResetContractVersion,
            CommandFamily.PetBasicSavvyReset =>
                PetBasicSavvyResetContractVersion,
            CommandFamily.PetRebirth => PetRebirthContractVersion,
            _ => ContractVersion
        };

    public static bool IsSupportedContractVersion(
        CommandFamily family,
        short version) =>
        family switch
        {
            CommandFamily.BagItemActivation =>
                version is ContractVersion or
                    PreviousBagItemActivationContractVersion or
                    BagItemActivationContractVersion,
            CommandFamily.PetToPetMerge =>
                version == PetToPetMergeContractVersion,
            CommandFamily.PetGrowthReset =>
                version is ContractVersion or
                    LegacyPetGrowthResetContractVersion or
                    PreviousPetGrowthResetContractVersion or
                    PetGrowthResetContractVersion,
            CommandFamily.PetBasicSavvyReset =>
                version == PetBasicSavvyResetContractVersion,
            CommandFamily.PetRebirth =>
                version is ContractVersion or PetRebirthContractVersion,
            _ => version == ContractVersion
        };

    private static PetDurableReceipt DecodeV1(ReadOnlySpan<byte> payload)
    {
        var stored = JsonSerializer.Deserialize<PersistedReceipt>(payload) ??
            throw new InvalidDataException(
                "The pet durable receipt is malformed.");
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
            stored.OutboxEventId);
    }

    private static PetDurableReceipt DecodePetToPetMerge(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedPetToPetMergeReceipt>(
                payload) ?? throw new InvalidDataException(
                    "The pet Merge durable receipt is malformed.");
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
            stored.DeputyPetId,
            stored.PetMergeDelta);
    }

    private static PetDurableReceipt DecodePetGrowthReset(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedPetGrowthReceipt>(
                payload) ?? throw new InvalidDataException(
                    "The pet Growth durable receipt is malformed.");
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
            GrowthPreview: stored.GrowthPreview);
    }

    private static PetDurableReceipt DecodePetBasicSavvyReset(
        ReadOnlySpan<byte> payload)
    {
        var stored =
            JsonSerializer.Deserialize<PersistedPetBasicSavvyReceipt>(
                payload) ?? throw new InvalidDataException(
                    "The pet Basic Savvy durable receipt is malformed.");
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
            BasicSavvyPreview: stored.BasicSavvyPreview);
    }

    public static PetDurableReceipt DecodeAndVerify(
        string payload,
        ReadOnlySpan<byte> expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var receipt = Decode(Encoding.UTF8.GetBytes(payload));
        var header = JsonSerializer.Deserialize<PersistedReceiptHeader>(
            payload) ?? throw new InvalidDataException(
                "The pet durable receipt is malformed.");
        var canonical = (receipt.Family, header.ContractVersion) switch
        {
            (CommandFamily.BagItemActivation, ContractVersion) or
            (CommandFamily.PetGrowthReset, ContractVersion) or
            (CommandFamily.PetRebirth, ContractVersion) =>
                EncodeV1(receipt),
            (CommandFamily.BagItemActivation,
                PreviousBagItemActivationContractVersion) =>
                EncodeBagItemActivationV2(receipt),
            (CommandFamily.PetGrowthReset,
                PreviousPetGrowthResetContractVersion) =>
                EncodePetGrowthV4(receipt),
            (CommandFamily.PetGrowthReset,
                LegacyPetGrowthResetContractVersion) =>
                EncodePetGrowthV3(receipt),
            _ => Encode(receipt)
        };
        var hash = SHA256.HashData(canonical);
        if (expectedHash.Length != hash.Length ||
            !CryptographicOperations.FixedTimeEquals(hash, expectedHash))
        {
            throw new InvalidDataException(
                "The pet durable receipt hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static PersistedReceiptHeader ReadHeader(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The pet durable receipt has an invalid size.");
        }

        return JsonSerializer.Deserialize<PersistedReceiptHeader>(payload) ??
            throw new InvalidDataException(
                "The pet durable receipt is malformed.");
    }

    private static byte[] EncodeV1(PetDurableReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new PersistedReceipt(
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
                receipt.OutboxEventId));

    private sealed record PersistedReceipt(
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
        Guid? OutboxEventId);

    private sealed record PersistedReceiptHeader(
        short ContractVersion,
        ushort Family);

    private sealed record PersistedPetToPetMergeReceipt(
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
        long DeputyPetId,
        PetToPetMergeDelta? PetMergeDelta);

    private sealed record PersistedPetGrowthReceipt(
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
        PetGrowthPreviewSnapshot? GrowthPreview);

    private sealed record PersistedPetBasicSavvyReceipt(
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
        PetBasicSavvyPreviewSnapshot? BasicSavvyPreview);

}
