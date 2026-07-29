using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class GearMentorDecomposePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommandFamilyCode = "gear_mentor_decompose";
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode = "terminal_rejected";
    public const string EventType =
        "inventory.gear_mentor_gear_decomposed";
    public const string LedgerReasonCode = "gear_mentor_decompose";
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string OrderingPolicy = "strict";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(
        GearMentorDecomposeGearResultStatus status) =>
        status == GearMentorDecomposeGearResultStatus.Succeeded
            ? CommittedResultCode
            : TerminalRejectedResultCode;

    public static byte[] Encode(
        GearMentorDecomposeGearExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(768);
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
            writer.WriteStartArray("selections");
            foreach (var selection in receipt.Selections)
            {
                writer.WriteStartObject();
                writer.WriteNumber(
                    "selectedKitBagSlot",
                    selection.SelectedKitBagSlot);
                writer.WriteNumber(
                    "sourceItemId",
                    selection.SourceItemId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("dustOutcomes");
            foreach (var outcome in receipt.DustOutcomes)
            {
                writer.WriteStartObject();
                writer.WriteNumber(
                    "selectedKitBagSlot",
                    outcome.SelectedKitBagSlot);
                writer.WriteNumber("dustItemId", outcome.DustItemId);
                writer.WriteNumber("quantity", outcome.Quantity);
                writer.WriteNumber("bound", outcome.Bound);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
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

    public static GearMentorDecomposeGearExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion ||
            (CommandFamily)root.GetProperty("family").GetUInt16() !=
                CommandFamily.GearMentorDecomposeGear)
        {
            throw new InvalidDataException(
                "The stored Decompose contract is unsupported.");
        }

        var selectionsElement = root.GetProperty("selections");
        if (selectionsElement.ValueKind != JsonValueKind.Array ||
            selectionsElement.GetArrayLength() is
                < GearMentorDecomposeGearCommandEnvelope
                    .MinimumSelectionCount or
                > GearMentorDecomposeGearCommandEnvelope
                    .MaximumSelectionCount)
        {
            throw new InvalidDataException(
                "The stored Decompose selections are invalid.");
        }

        var selections =
            new List<GearMentorDecomposeReceiptSelection>(
                selectionsElement.GetArrayLength());
        foreach (var element in selectionsElement.EnumerateArray())
        {
            selections.Add(
                new GearMentorDecomposeReceiptSelection(
                    element.GetProperty("selectedKitBagSlot").GetInt32(),
                    element.GetProperty("sourceItemId").GetUInt32()));
        }

        var outcomesElement = root.GetProperty("dustOutcomes");
        if (outcomesElement.ValueKind != JsonValueKind.Array ||
            outcomesElement.GetArrayLength() >
                GearMentorDecomposeGearCommandEnvelope
                    .MaximumSelectionCount)
        {
            throw new InvalidDataException(
                "The stored Decompose Dust outcomes are invalid.");
        }

        var outcomes =
            new List<GearMentorDecomposeDustOutcome>(
                outcomesElement.GetArrayLength());
        foreach (var element in outcomesElement.EnumerateArray())
        {
            outcomes.Add(
                new GearMentorDecomposeDustOutcome(
                    element.GetProperty("selectedKitBagSlot").GetInt32(),
                    element.GetProperty("dustItemId").GetUInt32(),
                    element.GetProperty("quantity").GetInt32(),
                    element.GetProperty("bound").GetInt16()));
        }

        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored Decompose event ID is invalid.")
        };
        return new GearMentorDecomposeGearExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            (GearMentorDecomposeGearResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            selections,
            outcomes,
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored Decompose result has no audit reference."),
            eventId);
    }

    public static GearMentorDecomposeGearExecutionReceipt DecodeAndVerify(
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
                "The stored Decompose result identity is inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored Decompose result hash is invalid.");
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
                "The canonical Decompose result exceeds its bound.");
        }
    }
}
