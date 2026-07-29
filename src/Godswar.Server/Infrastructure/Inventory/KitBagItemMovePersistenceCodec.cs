using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class KitBagItemMovePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType =
        "inventory.kit_bag_item_moved";
    public const string OrderingPolicy = "strict";
    public const string CommandFamilyCode = "kit_bag_item_move";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string LedgerReasonCode = "client_bag_move";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(
        KitBagItemMoveResultStatus status) =>
        status switch
        {
            KitBagItemMoveResultStatus.Moved => "moved",
            KitBagItemMoveResultStatus.Swapped => "swapped",
            KitBagItemMoveResultStatus.EmptySource => "empty_source",
            KitBagItemMoveResultStatus.StaleSource => "stale_source",
            KitBagItemMoveResultStatus.StaleDestination =>
                "stale_destination",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(
        KitBagItemMoveExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(1_536);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber(
                "sourceKitBagSlot",
                receipt.SourceKitBagSlot);
            writer.WriteNumber(
                "destinationKitBagSlot",
                receipt.DestinationKitBagSlot);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteString(
                "expectedSourceCompactItemState",
                receipt.ExpectedSourceCompactItemState);
            writer.WriteString(
                "expectedDestinationCompactItemState",
                receipt.ExpectedDestinationCompactItemState);
            writer.WriteString(
                "authoritativeSourceCompactItemState",
                receipt.AuthoritativeSourceCompactItemState);
            writer.WriteString(
                "authoritativeDestinationCompactItemState",
                receipt.AuthoritativeDestinationCompactItemState);
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

    public static KitBagItemMoveExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored kit-bag move result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored kit-bag move contract is unsupported.");
        }

        var outbox = root.GetProperty("outboxEventId");
        return new KitBagItemMoveExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("sourceKitBagSlot").GetInt32(),
            root.GetProperty("destinationKitBagSlot").GetInt32(),
            (KitBagItemMoveResultStatus)
                root.GetProperty("status").GetByte(),
            RequiredString(
                root,
                "expectedSourceCompactItemState"),
            RequiredString(
                root,
                "expectedDestinationCompactItemState"),
            RequiredString(
                root,
                "authoritativeSourceCompactItemState"),
            RequiredString(
                root,
                "authoritativeDestinationCompactItemState"),
            root.GetProperty("inventoryRevision").GetInt64(),
            RequiredString(root, "auditReference"),
            outbox.ValueKind == JsonValueKind.Null
                ? null
                : outbox.GetGuid());
    }

    public static KitBagItemMoveExecutionReceipt DecodeAndVerify(
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
                "The stored kit-bag move result hash is invalid.");
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
            $"The stored move result has no {name}.");

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical kit-bag move result exceeds its bound.");
        }
    }
}
