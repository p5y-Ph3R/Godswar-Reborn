using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class HolyStonePersistenceCodec
{
    public const short ContractVersion = 2;
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string EventType = "inventory.holy_stone_changed";
    public const string OrderingPolicy = "strict";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string CommandFamilyCode(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount => "holy_stone_mount",
            HolyStoneCommandOperation.Remove => "holy_stone_remove",
            HolyStoneCommandOperation.Drill => "holy_stone_drill",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static string LedgerReasonCode(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount => "holy_stone_mount",
            HolyStoneCommandOperation.Remove => "holy_stone_remove",
            HolyStoneCommandOperation.Drill => "holy_stone_drill",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static string ResultCode(
        HolyStoneCommandResultStatus status) =>
        status switch
        {
            HolyStoneCommandResultStatus.Mounted => "mounted",
            HolyStoneCommandResultStatus.Removed => "removed",
            HolyStoneCommandResultStatus.Drilled => "drilled",
            HolyStoneCommandResultStatus.WrongSelection =>
                "wrong_selection",
            HolyStoneCommandResultStatus.TargetNotEquipment =>
                "target_not_equipment",
            HolyStoneCommandResultStatus.StoneNotHolyStone =>
                "stone_not_holy_stone",
            HolyStoneCommandResultStatus.SocketNotDrilled =>
                "socket_not_drilled",
            HolyStoneCommandResultStatus.StoneMissingSpirit =>
                "stone_missing_spirit",
            HolyStoneCommandResultStatus.SocketCapacityReached =>
                "socket_capacity_reached",
            HolyStoneCommandResultStatus.IncompatibleTarget =>
                "incompatible_target",
            HolyStoneCommandResultStatus.InvalidSocket =>
                "invalid_socket",
            HolyStoneCommandResultStatus.SocketEmpty => "socket_empty",
            HolyStoneCommandResultStatus.BagFull => "bag_full",
            HolyStoneCommandResultStatus.MaximumSockets =>
                "maximum_sockets",
            HolyStoneCommandResultStatus.InsufficientFunds =>
                "insufficient_funds",
            HolyStoneCommandResultStatus.DuplicateSpirit =>
                "duplicate_spirit",
            HolyStoneCommandResultStatus.StaleTarget => "stale_target",
            HolyStoneCommandResultStatus.StaleStone => "stale_stone",
            HolyStoneCommandResultStatus.TargetMissing =>
                "target_missing",
            HolyStoneCommandResultStatus.StoneMissing =>
                "stone_missing",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(HolyStoneExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(2_048);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("operation", (byte)receipt.Operation);
            writer.WriteNumber("npcId", receipt.NpcId);
            writer.WriteNumber("dialogIndex", receipt.DialogIndex);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber(
                "nativeResultSubId",
                receipt.NativeResultSubId);
            writer.WriteNumber(
                "targetLocation",
                (byte)receipt.TargetLocation);
            writer.WriteNumber("targetSlot", receipt.TargetSlot);
            writer.WriteNumber("socketIndex", receipt.SocketIndex);
            WriteNullableNumber(
                writer,
                "targetItemInstanceId",
                receipt.TargetItemInstanceId);
            writer.WriteString(
                "expectedTargetCompactItemState",
                receipt.ExpectedTargetCompactItemState);
            writer.WriteString(
                "authoritativeTargetBeforeCompactItemState",
                receipt.AuthoritativeTargetBeforeCompactItemState);
            writer.WriteString(
                "authoritativeTargetAfterCompactItemState",
                receipt.AuthoritativeTargetAfterCompactItemState);
            writer.WriteNumber(
                "stoneKitBagSlot",
                receipt.StoneKitBagSlot);
            WriteNullableNumber(
                writer,
                "stoneItemInstanceId",
                receipt.StoneItemInstanceId);
            writer.WriteString(
                "expectedStoneCompactItemState",
                receipt.ExpectedStoneCompactItemState);
            writer.WriteString(
                "authoritativeStoneBeforeCompactItemState",
                receipt.AuthoritativeStoneBeforeCompactItemState);
            writer.WriteString(
                "authoritativeStoneAfterCompactItemState",
                receipt.AuthoritativeStoneAfterCompactItemState);
            writer.WriteNumber(
                "outputKitBagSlot",
                receipt.OutputKitBagSlot);
            WriteNullableNumber(
                writer,
                "outputItemInstanceId",
                receipt.OutputItemInstanceId);
            WriteNullableString(
                writer,
                "outputBeforeCompactItemState",
                receipt.OutputBeforeCompactItemState);
            WriteNullableString(
                writer,
                "outputAfterCompactItemState",
                receipt.OutputAfterCompactItemState);
            writer.WriteNumber("goldSpent", receipt.GoldSpent);
            writer.WriteNumber("goldBefore", receipt.GoldBefore);
            writer.WriteNumber("goldAfter", receipt.GoldAfter);
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

    public static HolyStoneExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored Holy Stone result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Holy Stone result contract is unsupported.");
        }

        var outbox = root.GetProperty("outboxEventId");
        return new HolyStoneExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            (HolyStoneCommandOperation)
                root.GetProperty("operation").GetByte(),
            root.GetProperty("npcId").GetInt32(),
            root.GetProperty("dialogIndex").GetInt32(),
            (HolyStoneCommandResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            (HolyStoneTargetLocation)
                root.GetProperty("targetLocation").GetByte(),
            root.GetProperty("targetSlot").GetInt32(),
            root.GetProperty("socketIndex").GetInt32(),
            NullableInt64(root, "targetItemInstanceId"),
            RequiredString(root, "expectedTargetCompactItemState"),
            RequiredString(
                root,
                "authoritativeTargetBeforeCompactItemState"),
            RequiredString(
                root,
                "authoritativeTargetAfterCompactItemState"),
            root.GetProperty("stoneKitBagSlot").GetInt32(),
            NullableInt64(root, "stoneItemInstanceId"),
            RequiredString(root, "expectedStoneCompactItemState"),
            RequiredString(
                root,
                "authoritativeStoneBeforeCompactItemState"),
            RequiredString(
                root,
                "authoritativeStoneAfterCompactItemState"),
            root.GetProperty("outputKitBagSlot").GetInt32(),
            NullableInt64(root, "outputItemInstanceId"),
            NullableString(root, "outputBeforeCompactItemState"),
            NullableString(root, "outputAfterCompactItemState"),
            root.GetProperty("goldSpent").GetInt32(),
            root.GetProperty("goldBefore").GetInt32(),
            root.GetProperty("goldAfter").GetInt32(),
            root.GetProperty("walletRevision").GetInt64(),
            root.GetProperty("inventoryRevision").GetInt64(),
            RequiredString(root, "auditReference"),
            outbox.ValueKind == JsonValueKind.Null
                ? null
                : outbox.GetGuid());
    }

    public static HolyStoneExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId,
        HolyStoneCommandOperation expectedOperation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash) ||
            receipt.Operation != expectedOperation ||
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
                "The stored Holy Stone result evidence is invalid.");
        }
        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static string RequiredString(
        JsonElement root,
        string name) =>
        root.GetProperty(name).GetString() ??
        throw new InvalidDataException(
            $"The stored Holy Stone result has no {name}.");

    private static string? NullableString(
        JsonElement root,
        string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetString() ??
                throw new InvalidDataException(
                    $"The stored Holy Stone result has invalid {name}.");
    }

    private static long? NullableInt64(
        JsonElement root,
        string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetInt64();
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string name,
        long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical Holy Stone result exceeds its bound.");
        }
    }
}
