using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class MakeAttributeStonePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode =
        "terminal_rejected";
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType =
        "inventory.gear_mentor_attribute_stone_made";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily =
        "gear_mentor_make_attribute_stone";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string LedgerReasonCode =
        "gear_mentor_make_attribute_stone";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(
        MakeAttributeStoneResultStatus status) =>
        status == MakeAttributeStoneResultStatus.Succeeded
            ? CommittedResultCode
            : TerminalRejectedResultCode;

    public static byte[] Encode(
        MakeAttributeStoneExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(384);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber(
                "nativeResultSubId",
                receipt.NativeResultSubId);
            writer.WriteNumber(
                "selectedKitBagSlot",
                receipt.SelectedKitBagSlot);
            writer.WriteNumber(
                "sourceDustItemId",
                receipt.SourceDustItemId);
            writer.WriteNumber(
                "outputStoneItemId",
                receipt.OutputStoneItemId);
            if (receipt.IsBound.HasValue)
            {
                writer.WriteBoolean("isBound", receipt.IsBound.Value);
            }
            else
            {
                writer.WriteNull("isBound");
            }

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

    public static MakeAttributeStoneExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Make Attribute Stone result contract is " +
                "unsupported.");
        }

        var boundElement = root.GetProperty("isBound");
        bool? isBound = boundElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored Make Attribute Stone binding is invalid.")
        };
        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored Make Attribute Stone event ID is invalid.")
        };

        return new MakeAttributeStoneExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            (MakeAttributeStoneResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            root.GetProperty("selectedKitBagSlot").GetInt32(),
            root.GetProperty("sourceDustItemId").GetUInt32(),
            root.GetProperty("outputStoneItemId").GetUInt32(),
            isBound,
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored Make Attribute Stone result has no " +
                    "audit reference."),
            eventId);
    }

    public static MakeAttributeStoneExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        if (!string.Equals(
                ResultCode(receipt.Status),
                expectedResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored Make Attribute Stone result identity is " +
                "inconsistent.");
        }

        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored Make Attribute Stone result hash is " +
                "invalid.");
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
                "The canonical Make Attribute Stone result exceeds " +
                "its bound.");
        }
    }
}
