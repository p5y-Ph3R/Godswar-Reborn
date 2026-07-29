using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class EquipmentForgePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode = "terminal_rejected";
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType = "inventory.equipment_forged";
    public const string OrderingPolicy = "strict";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string CommandFamilyCode = "equipment_forge";
    public const string LedgerReasonCode = "equipment_forge";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(
        EquipmentForgeCommandResultStatus status) =>
        status is EquipmentForgeCommandResultStatus.Succeeded or
            EquipmentForgeCommandResultStatus.FailedRoll
            ? CommittedResultCode
            : TerminalRejectedResultCode;

    public static byte[] Encode(EquipmentForgeExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        EnsureCanonicalEquipmentEvidence(receipt);
        var buffer = new ArrayBufferWriter<byte>(2_048);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber("materialType", receipt.MaterialType);
            writer.WriteNumber("roll", receipt.Roll);
            writer.WriteNumber(
                "successProbability",
                receipt.SuccessProbability);
            writer.WriteNumber("silverSpent", receipt.SilverSpent);
            writer.WriteString(
                "equipmentBefore",
                receipt.EquipmentBeforeCompactItemState);
            writer.WriteString(
                "equipmentAfter",
                receipt.EquipmentAfterCompactItemState);
            writer.WriteStartArray("materials");
            foreach (var material in receipt.Materials)
            {
                writer.WriteStartObject();
                writer.WriteNumber("role", (byte)material.Role);
                writer.WriteNumber("kitBagSlot", material.KitBagSlot);
                writer.WriteNumber("itemId", material.ItemId);
                writer.WriteNumber("quantity", material.Quantity);
                writer.WriteNumber("stackBefore", material.StackBefore);
                writer.WriteNumber("stackAfter", material.StackAfter);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber(
                "walletRevision",
                receipt.WalletRevision);
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

    public static EquipmentForgeExecutionReceipt Decode(
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
                "The stored equipment-forge contract is unsupported.");
        }

        var materialsElement = root.GetProperty("materials");
        if (materialsElement.ValueKind != JsonValueKind.Array ||
            materialsElement.GetArrayLength() is < 0 or >
                EquipmentForgeCommandEnvelope.MaximumOddsQuantity + 1)
        {
            throw new InvalidDataException(
                "The stored equipment-forge materials are invalid.");
        }

        var materials = new List<EquipmentForgeReceiptMaterial>(
            materialsElement.GetArrayLength());
        foreach (var element in materialsElement.EnumerateArray())
        {
            materials.Add(new EquipmentForgeReceiptMaterial(
                (EquipmentForgeCommandItemRole)
                    element.GetProperty("role").GetByte(),
                element.GetProperty("kitBagSlot").GetInt32(),
                element.GetProperty("itemId").GetUInt32(),
                element.GetProperty("quantity").GetInt32(),
                element.GetProperty("stackBefore").GetInt16(),
                element.GetProperty("stackAfter").GetInt16()));
        }

        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored equipment-forge event ID is invalid.")
        };
        var receipt = new EquipmentForgeExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            (EquipmentForgeCommandResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("materialType").GetInt32(),
            root.GetProperty("roll").GetInt32(),
            root.GetProperty("successProbability").GetInt32(),
            root.GetProperty("silverSpent").GetInt32(),
            root.GetProperty("equipmentBefore").GetString() ?? string.Empty,
            root.GetProperty("equipmentAfter").GetString() ?? string.Empty,
            materials,
            root.GetProperty("walletRevision").GetInt64(),
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored result has no audit reference."),
            eventId);
        EnsureCanonicalEquipmentEvidence(receipt);
        return receipt;
    }

    public static EquipmentForgeExecutionReceipt DecodeAndVerify(
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
                "The stored equipment-forge result identity is inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored equipment-forge result hash is invalid.");
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
                "The canonical equipment-forge result exceeds its bound.");
        }
    }

    private static void EnsureCanonicalEquipmentEvidence(
        EquipmentForgeExecutionReceipt receipt)
    {
        if (!receipt.Committed)
        {
            return;
        }

        EnsureCanonicalEquipmentState(
            receipt.EquipmentBeforeCompactItemState);
        EnsureCanonicalEquipmentState(
            receipt.EquipmentAfterCompactItemState);
    }

    private static void EnsureCanonicalEquipmentState(string value)
    {
        var item = CompactItemEntry.Parse(value);
        if (item.IsEmpty ||
            item.Stack != 1 ||
            !string.Equals(
                item.ToCompactString(),
                value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Forge equipment evidence must be canonical and nonempty.");
        }
    }
}
