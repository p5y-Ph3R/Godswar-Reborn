using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Warehouse;

namespace Godswar.Server.Infrastructure.Warehouse;

internal static class WarehouseExpansionPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommandFamilyCode = "warehouse_expansion";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string InventoryConsumerKey = "inventory_projection_v1";
    public const string InventoryAggregateType = "character_inventory";
    public const string InventoryEventType =
        "inventory.warehouse_key_consumed";
    public const string WarehouseConsumerKey = "warehouse_projection_v1";
    public const string WarehouseAggregateType = "character_warehouse";
    public const string WarehouseEventType = "warehouse.capacity_expanded";
    public const string OrderingPolicy = "strict";
    public const string LedgerReasonCode = "warehouse_expansion";

    public static string InventoryAggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string WarehouseAggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:warehouse");

    public static string ResultCode(WarehouseExpansionResultStatus status) =>
        status switch
        {
            WarehouseExpansionResultStatus.Expanded => "expanded",
            WarehouseExpansionResultStatus.InsufficientKeys =>
                "insufficient_keys",
            WarehouseExpansionResultStatus.AlreadyMaximum =>
                "already_maximum",
            WarehouseExpansionResultStatus.CapacityConflict =>
                "capacity_conflict",
            WarehouseExpansionResultStatus.ConcurrentConflict =>
                "concurrent_conflict",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(WarehouseExpansionExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        var buffer = new ArrayBufferWriter<byte>(2_048);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("realmId", receipt.RealmId);
            writer.WriteNumber("actionSubId", receipt.ActionSubId);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber("previousCapacity", receipt.PreviousCapacity);
            writer.WriteNumber("currentCapacity", receipt.CurrentCapacity);
            writer.WriteNumber("keyItemId", receipt.KeyItemId);
            writer.WriteNumber("requiredKeyCount", receipt.RequiredKeyCount);
            writer.WriteNumber("consumedKeyCount", receipt.ConsumedKeyCount);
            writer.WriteNumber("policyRevision", receipt.PolicyRevision);
            writer.WriteString("policySha256", receipt.PolicySha256);
            writer.WriteNumber(
                "warehouseRevision",
                receipt.WarehouseRevision);
            writer.WriteNumber(
                "inventoryRevision",
                receipt.InventoryRevision);
            writer.WritePropertyName("keyMutations");
            WarehouseTransferPersistenceCodec.WriteMutations(
                writer,
                receipt.KeyMutations);
            writer.WriteString("auditReference", receipt.AuditReference);
            if (receipt.OutboxEventId.HasValue)
            {
                writer.WriteString("outboxEventId", receipt.OutboxEventId.Value);
            }
            else
            {
                writer.WriteNull("outboxEventId");
            }
            writer.WriteEndObject();
        }
        EnsureBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static WarehouseExpansionExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsureBound(payload.Length);
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt16() != ContractVersion)
        {
            throw new InvalidDataException(
                "The stored warehouse expansion contract is unsupported.");
        }
        var outbox = root.GetProperty("outboxEventId");
        var receipt = new WarehouseExpansionExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("realmId").GetInt32(),
            root.GetProperty("actionSubId").GetInt32(),
            (WarehouseExpansionResultStatus)root.GetProperty("status").GetByte(),
            root.GetProperty("previousCapacity").GetInt32(),
            root.GetProperty("currentCapacity").GetInt32(),
            root.GetProperty("keyItemId").GetInt32(),
            root.GetProperty("requiredKeyCount").GetInt32(),
            root.GetProperty("consumedKeyCount").GetInt32(),
            root.GetProperty("policyRevision").GetInt64(),
            RequiredString(root, "policySha256"),
            root.GetProperty("warehouseRevision").GetInt64(),
            root.GetProperty("inventoryRevision").GetInt64(),
            WarehouseTransferPersistenceCodec.ReadMutations(
                root.GetProperty("keyMutations")),
            RequiredString(root, "auditReference"),
            outbox.ValueKind == JsonValueKind.Null ? null : outbox.GetGuid());
        receipt.Validate();
        return receipt;
    }

    public static WarehouseExpansionExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        var canonical = Encode(receipt);
        var hash = Hash(canonical);
        if (expectedHash.Length != hash.Length ||
            !CryptographicOperations.FixedTimeEquals(hash, expectedHash))
        {
            throw new InvalidDataException(
                "The stored warehouse expansion receipt hash is invalid.");
        }
        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ??
        throw new InvalidDataException($"Warehouse receipt has no {name}.");

    private static void EnsureBound(int count)
    {
        if (count is <= 0 or > OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The warehouse expansion receipt exceeds its payload bound.");
        }
    }
}
