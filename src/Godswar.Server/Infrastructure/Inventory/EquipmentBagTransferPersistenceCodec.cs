using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class EquipmentBagTransferPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType =
        "inventory.equipment_bag_transferred";
    public const string OrderingPolicy = "strict";
    public const string CommandFamilyCode =
        "equipment_bag_transfer";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string LedgerReasonCode =
        "client_equipment_bag_transfer";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(
        EquipmentBagTransferResultStatus status) =>
        status switch
        {
            EquipmentBagTransferResultStatus.Equipped => "equipped",
            EquipmentBagTransferResultStatus.Unequipped =>
                "unequipped",
            EquipmentBagTransferResultStatus.StaleEquipment =>
                "stale_equipment",
            EquipmentBagTransferResultStatus.StaleKitBag =>
                "stale_kit_bag",
            EquipmentBagTransferResultStatus.BothEmpty =>
                "both_empty",
            EquipmentBagTransferResultStatus.BothOccupied =>
                "both_occupied",
            EquipmentBagTransferResultStatus.ItemNotEquipment =>
                "item_not_equipment",
            EquipmentBagTransferResultStatus.WrongEquipmentSlot =>
                "wrong_equipment_slot",
            EquipmentBagTransferResultStatus.ProfessionRestricted =>
                "profession_restricted",
            EquipmentBagTransferResultStatus.LevelRestricted =>
                "level_restricted",
            EquipmentBagTransferResultStatus
                .MountDependencyBlocked =>
                "mount_dependency_blocked",
            EquipmentBagTransferResultStatus.MountUnsupported =>
                "mount_unsupported",
            EquipmentBagTransferResultStatus.RideRuntimeBlocked =>
                "ride_runtime_blocked",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(
        EquipmentBagTransferExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(1_536);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber(
                "equipmentSlot",
                receipt.EquipmentSlot);
            writer.WriteNumber("kitBagSlot", receipt.KitBagSlot);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteString(
                "expectedEquipmentCompactItemState",
                receipt.ExpectedEquipmentCompactItemState);
            writer.WriteString(
                "expectedKitBagCompactItemState",
                receipt.ExpectedKitBagCompactItemState);
            writer.WriteString(
                "authoritativeEquipmentCompactItemState",
                receipt.AuthoritativeEquipmentCompactItemState);
            writer.WriteString(
                "authoritativeKitBagCompactItemState",
                receipt.AuthoritativeKitBagCompactItemState);
            writer.WriteNumber(
                "inventoryRevision",
                receipt.InventoryRevision);
            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            if (receipt.OutboxEventId.HasValue)
            {
                writer.WriteString(
                    "outboxEventId",
                    receipt.OutboxEventId.Value);
            }
            else
            {
                writer.WriteNull("outboxEventId");
            }
            writer.WriteEndObject();
        }

        EnsurePayloadBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static EquipmentBagTransferExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored equipment transfer result has an " +
                "invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored equipment transfer contract is " +
                "unsupported.");
        }

        var outbox = root.GetProperty("outboxEventId");
        return new EquipmentBagTransferExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("equipmentSlot").GetInt32(),
            root.GetProperty("kitBagSlot").GetInt32(),
            (EquipmentBagTransferResultStatus)
                root.GetProperty("status").GetByte(),
            RequiredString(
                root,
                "expectedEquipmentCompactItemState"),
            RequiredString(
                root,
                "expectedKitBagCompactItemState"),
            RequiredString(
                root,
                "authoritativeEquipmentCompactItemState"),
            RequiredString(
                root,
                "authoritativeKitBagCompactItemState"),
            root.GetProperty("inventoryRevision").GetInt64(),
            RequiredString(root, "auditReference"),
            outbox.ValueKind == JsonValueKind.Null
                ? null
                : outbox.GetGuid());
    }

    public static EquipmentBagTransferExecutionReceipt
        DecodeAndVerify(
            string payloadJson,
            ReadOnlySpan<byte> expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored equipment transfer result hash is " +
                "invalid.");
        }
        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static string RequiredString(
        JsonElement root,
        string name) =>
        root.GetProperty(name).GetString() ??
        throw new InvalidDataException(
            $"The stored equipment transfer has no {name}.");

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical equipment transfer result exceeds " +
                "its bound.");
        }
    }
}
