using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class DeveloperItemGrantPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string PreconditionFailedResultCode =
        "precondition_failed";
    public const string InsufficientCapacityReasonCode =
        "insufficient_capacity";
    public const string ConsumerKey = "inventory_projection_v1";
    public const string AggregateType = "character_inventory";
    public const string LegacyMaterialEventType =
        "inventory.developer_material_granted";
    public const string EventType =
        "inventory.developer_item_granted";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "developer_item_grant";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static byte[] Encode(
        DeveloperItemGrantExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("itemId", receipt.ItemId);
            writer.WriteNumber(
                "grantedQuantity",
                receipt.GrantedQuantity);
            writer.WriteNumber(
                "inventoryRevision",
                receipt.InventoryRevision);
            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            writer.WriteString(
                "outboxEventId",
                receipt.OutboxEventId);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical inventory grant result exceeds its " +
                "durable bound.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static DeveloperItemGrantExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored inventory grant result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored inventory grant result contract is " +
                "unsupported.");
        }

        return new DeveloperItemGrantExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("itemId").GetUInt32(),
            root.GetProperty("grantedQuantity").GetInt32(),
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored inventory grant result has no audit " +
                    "reference."),
            root.GetProperty("outboxEventId").GetGuid());
    }

    public static DeveloperItemGrantExecutionReceipt DecodeAndVerify(
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
                "The stored inventory grant result hash is invalid.");
        }

        return receipt;
    }

    public static byte[] EncodeInsufficientCapacity(
        int characterId,
        uint itemId,
        int quantity,
        long auditId)
    {
        if (characterId <= 0 ||
            itemId is < DeveloperItemGrantCommandEnvelope.MinimumItemId or
                > DeveloperItemGrantCommandEnvelope.MaximumItemId ||
            quantity is < DeveloperItemGrantCommandEnvelope.MinimumQuantity or
                > DeveloperItemGrantCommandEnvelope.MaximumQuantity ||
            auditId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterId),
                "The terminal grant result is invalid.");
        }

        var buffer = new ArrayBufferWriter<byte>(192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteString(
                "resultCode",
                PreconditionFailedResultCode);
            writer.WriteString(
                "reasonCode",
                InsufficientCapacityReasonCode);
            writer.WriteNumber("characterId", characterId);
            writer.WriteNumber("itemId", itemId);
            writer.WriteNumber("quantity", quantity);
            writer.WriteString(
                "auditReference",
                auditId.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        EnsurePayloadBound(
            buffer.WrittenCount,
            "terminal inventory grant");
        return buffer.WrittenSpan.ToArray();
    }

    public static void ValidateInsufficientCapacity(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        int expectedCharacterId,
        uint expectedItemId,
        int expectedQuantity,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion ||
            !string.Equals(
                root.GetProperty("resultCode").GetString(),
                PreconditionFailedResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                root.GetProperty("reasonCode").GetString(),
                InsufficientCapacityReasonCode,
                StringComparison.Ordinal) ||
            root.GetProperty("characterId").GetInt32() !=
                expectedCharacterId ||
            root.GetProperty("itemId").GetUInt32() != expectedItemId ||
            root.GetProperty("quantity").GetInt32() !=
                expectedQuantity ||
            !string.Equals(
                root.GetProperty("auditReference").GetString(),
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored terminal inventory grant is invalid.");
        }

        var canonical = EncodeInsufficientCapacity(
            expectedCharacterId,
            expectedItemId,
            expectedQuantity,
            expectedAuditId);
        VerifyHash(
            canonical,
            expectedHash,
            "terminal inventory grant");
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void EnsurePayloadBound(
        int payloadBytes,
        string description)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"The canonical {description} result exceeds its bound.");
        }
    }

    private static void VerifyHash(
        ReadOnlySpan<byte> canonical,
        ReadOnlySpan<byte> expectedHash,
        string description)
    {
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                $"The stored {description} result hash is invalid.");
        }
    }
}
