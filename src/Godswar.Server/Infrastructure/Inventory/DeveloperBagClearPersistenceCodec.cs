using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class DeveloperBagClearPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string PreconditionFailedResultCode =
        "precondition_failed";
    public const string EmptyBagReasonCode = "empty_bag";
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType = "inventory.developer_bag_cleared";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "developer_bag_clear";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static byte[] Encode(
        DeveloperBagClearExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteStartArray("removedSlots");
            foreach (var slot in receipt.RemovedSlots)
            {
                writer.WriteNumberValue(slot);
            }

            writer.WriteEndArray();
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
                "The canonical bag-clear result exceeds its bound.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static DeveloperBagClearExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored bag-clear result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored bag-clear result contract is unsupported.");
        }

        var slotElement = root.GetProperty("removedSlots");
        if (slotElement.ValueKind != JsonValueKind.Array ||
            slotElement.GetArrayLength() >
                DeveloperBagClearExecutionReceipt.MaximumRemovedSlots)
        {
            throw new InvalidDataException(
                "The stored bag-clear slot list is invalid.");
        }

        var slots = slotElement
            .EnumerateArray()
            .Select(static value => value.GetInt16())
            .ToArray();
        return new DeveloperBagClearExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            slots,
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored bag-clear result has no audit reference."),
            root.GetProperty("outboxEventId").GetGuid());
    }

    public static DeveloperBagClearExecutionReceipt DecodeAndVerify(
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
                "The stored bag-clear result hash is invalid.");
        }

        return receipt;
    }

    public static byte[] EncodeEmptyBag(
        int characterId,
        long auditId)
    {
        if (characterId <= 0 || auditId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterId),
                "The terminal bag-clear result is invalid.");
        }

        var buffer = new ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteString(
                "resultCode",
                PreconditionFailedResultCode);
            writer.WriteString("reasonCode", EmptyBagReasonCode);
            writer.WriteNumber("characterId", characterId);
            writer.WriteString(
                "auditReference",
                auditId.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        EnsurePayloadBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static void ValidateEmptyBag(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        int expectedCharacterId,
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
                EmptyBagReasonCode,
                StringComparison.Ordinal) ||
            root.GetProperty("characterId").GetInt32() !=
                expectedCharacterId ||
            !string.Equals(
                root.GetProperty("auditReference").GetString(),
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored terminal bag-clear result is invalid.");
        }

        var canonical =
            EncodeEmptyBag(expectedCharacterId, expectedAuditId);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored terminal bag-clear result hash is invalid.");
        }
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical terminal bag-clear result exceeds its " +
                "bound.");
        }
    }
}
