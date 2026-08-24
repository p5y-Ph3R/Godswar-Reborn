using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Warehouse;

namespace Godswar.Server.Infrastructure.Warehouse;

internal static class WarehouseTransferPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ConsumerKey = "inventory_projection_v1";
    public const string AggregateType = "character_inventory";
    public const string EventType = "inventory.warehouse_transferred";
    public const string OrderingPolicy = "strict";
    public const string CommandFamilyCode = "warehouse_transfer";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string LedgerReasonCode = "warehouse_transfer";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string ResultCode(WarehouseTransferResultStatus status) =>
        status switch
        {
            WarehouseTransferResultStatus.Deposited => "deposited",
            WarehouseTransferResultStatus.Withdrawn => "withdrawn",
            WarehouseTransferResultStatus.InternalMoved => "internal_moved",
            WarehouseTransferResultStatus.Stacked => "stacked",
            WarehouseTransferResultStatus.Swapped => "swapped",
            WarehouseTransferResultStatus.EmptySource => "empty_source",
            WarehouseTransferResultStatus.DestinationOccupied =>
                "destination_occupied",
            WarehouseTransferResultStatus.BagFull => "bag_full",
            WarehouseTransferResultStatus.CapacityExceeded =>
                "capacity_exceeded",
            WarehouseTransferResultStatus.StackIncompatible =>
                "stack_incompatible",
            WarehouseTransferResultStatus.ConcurrentConflict =>
                "concurrent_conflict",
            WarehouseTransferResultStatus.RestrictedItem =>
                "restricted_item",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(WarehouseTransferExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        var buffer = new ArrayBufferWriter<byte>(2_048);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("operation", (byte)receipt.Operation);
            writer.WriteNumber("warehouseSlot", receipt.WarehouseSlot);
            writer.WriteNumber("kitBagSlot", receipt.KitBagSlot);
            writer.WriteNumber(
                "destinationWarehouseSlot",
                receipt.DestinationWarehouseSlot);
            writer.WriteNumber(
                "actualWarehouseSlot",
                receipt.ActualWarehouseSlot);
            writer.WriteNumber(
                "actualKitBagSlot",
                receipt.ActualKitBagSlot);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber("movedQuantity", receipt.MovedQuantity);
            writer.WriteNumber("capacity", receipt.Capacity);
            writer.WriteNumber(
                "warehouseRevision",
                receipt.WarehouseRevision);
            writer.WriteNumber(
                "inventoryRevision",
                receipt.InventoryRevision);
            writer.WritePropertyName("mutations");
            WriteMutations(writer, receipt.Mutations);
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

    public static WarehouseTransferExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsureBound(payload.Length);
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        RequireContract(root);
        var outbox = root.GetProperty("outboxEventId");
        var receipt = new WarehouseTransferExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            (WarehouseTransferOperation)root.GetProperty("operation").GetByte(),
            root.GetProperty("warehouseSlot").GetInt32(),
            root.GetProperty("kitBagSlot").GetInt32(),
            root.GetProperty("destinationWarehouseSlot").GetInt32(),
            root.GetProperty("actualWarehouseSlot").GetInt32(),
            root.GetProperty("actualKitBagSlot").GetInt32(),
            (WarehouseTransferResultStatus)root.GetProperty("status").GetByte(),
            root.GetProperty("movedQuantity").GetInt32(),
            root.GetProperty("capacity").GetInt32(),
            root.GetProperty("warehouseRevision").GetInt64(),
            root.GetProperty("inventoryRevision").GetInt64(),
            ReadMutations(root.GetProperty("mutations")),
            RequiredString(root, "auditReference"),
            outbox.ValueKind == JsonValueKind.Null ? null : outbox.GetGuid());
        receipt.Validate();
        return receipt;
    }

    public static WarehouseTransferExecutionReceipt DecodeAndVerify(
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
                "The stored warehouse transfer receipt hash is invalid.");
        }
        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    internal static void WriteMutations(
        Utf8JsonWriter writer,
        IReadOnlyList<WarehouseItemMutation> mutations)
    {
        writer.WriteStartArray();
        foreach (var mutation in mutations)
        {
            // Fixed-position arrays keep the proven 100-row native fan-out
            // comfortably below the global 16 KiB inbox/outbox payload cap.
            writer.WriteStartArray();
            writer.WriteNumberValue(mutation.ItemInstanceId);
            writer.WriteNumberValue(mutation.ItemId);
            writer.WriteNumberValue((byte)mutation.BeforeLocation);
            writer.WriteNumberValue(mutation.BeforeSlot);
            writer.WriteNumberValue(mutation.BeforeStack);
            if (mutation.AfterLocation.HasValue)
            {
                writer.WriteNumberValue((byte)mutation.AfterLocation.Value);
                writer.WriteNumberValue(mutation.AfterSlot!.Value);
                writer.WriteNumberValue(mutation.AfterStack!.Value);
            }
            else
            {
                writer.WriteNullValue();
                writer.WriteNullValue();
                writer.WriteNullValue();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    internal static IReadOnlyList<WarehouseItemMutation> ReadMutations(
        JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Warehouse mutations are invalid.");
        }
        var mutations = new List<WarehouseItemMutation>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Array ||
                element.GetArrayLength() != 8)
            {
                throw new InvalidDataException(
                    "A warehouse mutation tuple is invalid.");
            }
            var fields = element.EnumerateArray().ToArray();
            var afterLocation = fields[5];
            mutations.Add(new WarehouseItemMutation(
                fields[0].GetInt64(),
                fields[1].GetInt32(),
                (WarehouseInventoryLocation)fields[2].GetByte(),
                fields[3].GetInt32(),
                fields[4].GetInt32(),
                afterLocation.ValueKind == JsonValueKind.Null
                    ? null
                    : (WarehouseInventoryLocation)afterLocation.GetByte(),
                ReadNullableInt(fields[6]),
                ReadNullableInt(fields[7])));
        }
        return mutations;
    }

    private static int? ReadNullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    private static void RequireContract(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt16() != ContractVersion)
        {
            throw new InvalidDataException(
                "The stored warehouse transfer contract is unsupported.");
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ??
        throw new InvalidDataException($"Warehouse receipt has no {name}.");

    private static void EnsureBound(int count)
    {
        if (count is <= 0 or > OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The warehouse transfer receipt exceeds its payload bound.");
        }
    }
}
