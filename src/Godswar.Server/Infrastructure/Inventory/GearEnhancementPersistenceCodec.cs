using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class GearEnhancementPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode = "terminal_rejected";
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

    public static string CommandFamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorEnhanceAttribute =>
                "gear_mentor_enhance_attribute",
            CommandFamily.GearMentorAddAttribute =>
                "gear_mentor_add_attribute",
            CommandFamily.GearMentorDeleteAttribute =>
                "gear_mentor_delete_attribute",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string LedgerReasonCode(CommandFamily family) =>
        CommandFamilyCode(family);

    public static string EventType(CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorEnhanceAttribute =>
                "inventory.gear_mentor_attribute_enhanced",
            CommandFamily.GearMentorAddAttribute =>
                "inventory.gear_mentor_attribute_added",
            CommandFamily.GearMentorDeleteAttribute =>
                "inventory.gear_mentor_attribute_deleted",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static bool IsEventType(string eventType) =>
        string.Equals(
            eventType,
            EventType(CommandFamily.GearMentorEnhanceAttribute),
            StringComparison.Ordinal) ||
        string.Equals(
            eventType,
            EventType(CommandFamily.GearMentorAddAttribute),
            StringComparison.Ordinal) ||
        string.Equals(
            eventType,
            EventType(CommandFamily.GearMentorDeleteAttribute),
            StringComparison.Ordinal);

    public static string ResultCode(
        GearEnhancementCommandResultStatus status) =>
        status == GearEnhancementCommandResultStatus.Succeeded
            ? CommittedResultCode
            : TerminalRejectedResultCode;

    public static byte[] Encode(
        GearEnhancementExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(2_048);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("family", (ushort)receipt.Family);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("operation", (byte)receipt.Operation);
            writer.WriteNumber("npcId", receipt.NpcId);
            writer.WriteNumber("dialogIndex", receipt.DialogIndex);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber(
                "nativeResultSubId",
                receipt.NativeResultSubId);
            writer.WriteStartArray("mutations");
            foreach (var mutation in receipt.Mutations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("role", (byte)mutation.Role);
                writer.WriteNumber("kitBagSlot", mutation.KitBagSlot);
                writer.WriteNumber("itemId", mutation.ItemId);
                writer.WriteString(
                    "beforeCompactItemState",
                    mutation.BeforeCompactItemState);
                writer.WriteString(
                    "afterCompactItemState",
                    mutation.AfterCompactItemState);
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

    public static GearEnhancementExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var operation = (GearEnhancementCommandOperation)
            root.GetProperty("operation").GetByte();
        var family = (CommandFamily)
            root.GetProperty("family").GetUInt16();
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion ||
            family != GearEnhancementCommandEnvelope.Family(operation))
        {
            throw new InvalidDataException(
                "The stored Gear Enhancement contract is unsupported.");
        }

        var mutationsElement = root.GetProperty("mutations");
        if (mutationsElement.ValueKind != JsonValueKind.Array ||
            mutationsElement.GetArrayLength() > 3)
        {
            throw new InvalidDataException(
                "The stored Gear Enhancement mutations are invalid.");
        }

        var mutations =
            new List<GearEnhancementReceiptMutation>(
                mutationsElement.GetArrayLength());
        foreach (var element in mutationsElement.EnumerateArray())
        {
            mutations.Add(
                new GearEnhancementReceiptMutation(
                    (GearEnhancementCommandItemRole)
                        element.GetProperty("role").GetByte(),
                    element.GetProperty("kitBagSlot").GetInt32(),
                    element.GetProperty("itemId").GetUInt32(),
                    element.GetProperty("beforeCompactItemState")
                        .GetString() ??
                        throw new InvalidDataException(
                            "Mutation before-state is missing."),
                    element.GetProperty("afterCompactItemState")
                        .GetString() ??
                        throw new InvalidDataException(
                            "Mutation after-state is missing.")));
        }

        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored Gear Enhancement event ID is invalid.")
        };
        return new GearEnhancementExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            operation,
            root.GetProperty("npcId").GetInt32(),
            root.GetProperty("dialogIndex").GetInt32(),
            (GearEnhancementCommandResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            mutations,
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored result has no audit reference."),
            eventId);
    }

    public static GearEnhancementExecutionReceipt DecodeAndVerify(
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
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored Gear Enhancement result identity is " +
                "inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored Gear Enhancement result hash is invalid.");
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
                "The canonical Gear Enhancement result exceeds its " +
                "bound.");
        }
    }
}
