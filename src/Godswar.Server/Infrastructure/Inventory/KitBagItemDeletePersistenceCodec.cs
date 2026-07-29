using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class KitBagItemDeletePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType = "inventory.kit_bag_item_deleted";
    public const string OrderingPolicy = "strict";
    public const string CommandFamilyCode = "kit_bag_item_delete";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string LedgerReasonCode = "client_ground_delete";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(
        KitBagItemDeleteResultStatus status) =>
        status switch
        {
            KitBagItemDeleteResultStatus.Deleted => "deleted",
            KitBagItemDeleteResultStatus.EmptySlot => "empty_slot",
            KitBagItemDeleteResultStatus.StaleSelection =>
                "stale_selection",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(
        KitBagItemDeleteExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(768);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("kitBagSlot", receipt.KitBagSlot);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteString(
                "expectedCompactItemState",
                receipt.ExpectedCompactItemState);
            writer.WriteString(
                "authoritativeCompactItemState",
                receipt.AuthoritativeCompactItemState);
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

    public static KitBagItemDeleteExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored kit-bag delete result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored kit-bag delete contract is unsupported.");
        }

        var outboxElement = root.GetProperty("outboxEventId");
        var outboxEventId =
            outboxElement.ValueKind == JsonValueKind.Null
                ? (Guid?)null
                : outboxElement.GetGuid();
        return new KitBagItemDeleteExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("kitBagSlot").GetInt32(),
            (KitBagItemDeleteResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("expectedCompactItemState")
                .GetString() ??
                throw new InvalidDataException(
                    "The stored delete result has no expected state."),
            root.GetProperty("authoritativeCompactItemState")
                .GetString() ??
                throw new InvalidDataException(
                    "The stored delete result has no authoritative state."),
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored delete result has no audit reference."),
            outboxEventId);
    }

    public static KitBagItemDeleteExecutionReceipt DecodeAndVerify(
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
                "The stored kit-bag delete result hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical kit-bag delete result exceeds its bound.");
        }
    }
}
