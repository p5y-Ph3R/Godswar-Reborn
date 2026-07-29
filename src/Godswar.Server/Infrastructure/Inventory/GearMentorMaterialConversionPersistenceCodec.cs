using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class GearMentorMaterialConversionPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode =
        "terminal_rejected";
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string OrderingPolicy = "strict";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public const string TransformCommandFamily =
        "gear_mentor_transform_crystal";
    public const string CombineCommandFamily =
        "gear_mentor_combine_gem_pieces";
    public const string TransformEventType =
        "inventory.gear_mentor_crystal_transformed";
    public const string CombineEventType =
        "inventory.gear_mentor_gem_pieces_combined";
    public const string TransformLedgerReasonCode =
        "gear_mentor_transform_crystal";
    public const string CombineLedgerReasonCode =
        "gear_mentor_combine_gem_pieces";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string CommandFamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorTransformCrystal =>
                TransformCommandFamily,
            CommandFamily.GearMentorCombineGemPieces =>
                CombineCommandFamily,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string EventType(CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorTransformCrystal =>
                TransformEventType,
            CommandFamily.GearMentorCombineGemPieces =>
                CombineEventType,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string LedgerReasonCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorTransformCrystal =>
                TransformLedgerReasonCode,
            CommandFamily.GearMentorCombineGemPieces =>
                CombineLedgerReasonCode,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string ResultCode(
        GearMentorMaterialConversionResultStatus status) =>
        status == GearMentorMaterialConversionResultStatus.Succeeded
            ? CommittedResultCode
            : TerminalRejectedResultCode;

    public static byte[] Encode(
        GearMentorMaterialConversionExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(384);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("family", (ushort)receipt.Family);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber(
                "nativeResultSubId",
                receipt.NativeResultSubId);
            writer.WriteNumber(
                "selectedKitBagSlot",
                receipt.SelectedKitBagSlot);
            writer.WriteNumber("sourceItemId", receipt.SourceItemId);
            writer.WriteNumber("outputItemId", receipt.OutputItemId);
            writer.WriteNumber(
                "outputQuantity",
                receipt.OutputQuantity);
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

    public static GearMentorMaterialConversionExecutionReceipt Decode(
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
                "The stored material-conversion contract is unsupported.");
        }

        var boundElement = root.GetProperty("isBound");
        bool? isBound = boundElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored material binding is invalid.")
        };
        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored material event ID is invalid.")
        };

        return new GearMentorMaterialConversionExecutionReceipt(
            (CommandFamily)root.GetProperty("family").GetUInt16(),
            root.GetProperty("characterId").GetInt32(),
            (GearMentorMaterialConversionResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            root.GetProperty("selectedKitBagSlot").GetInt32(),
            root.GetProperty("sourceItemId").GetUInt32(),
            root.GetProperty("outputItemId").GetUInt32(),
            root.GetProperty("outputQuantity").GetInt32(),
            isBound,
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored material result has no audit reference."),
            eventId);
    }

    public static GearMentorMaterialConversionExecutionReceipt
        DecodeAndVerify(
            string payloadJson,
            ReadOnlySpan<byte> expectedHash,
            string expectedResultCode,
            long expectedAuditId,
            CommandFamily expectedFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        if (receipt.Family != expectedFamily ||
            !string.Equals(
                ResultCode(receipt.Status),
                expectedResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored material result identity is inconsistent.");
        }

        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored material result hash is invalid.");
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
                "The canonical material result exceeds its bound.");
        }
    }
}
