using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class HolySuitPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string PrincipalType = "account";
    public const string AggregateType = "character_inventory";
    public const string OrderingPolicy = "strict";
    public const string RetentionPolicy = "permanent";
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const int MaximumReceiptMutations = 96;

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string CommandFamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.HolySuitStoreExperience =>
                "holy_suit_store_experience",
            CommandFamily.HolySuitTransferExperience =>
                "holy_suit_transfer_experience",
            CommandFamily.HolySuitConsumeWare =>
                "holy_suit_consume_ware",
            CommandFamily.HolySuitTransformExperience =>
                "holy_suit_transform_experience",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string EventType(CommandFamily family) =>
        family switch
        {
            CommandFamily.HolySuitStoreExperience =>
                "inventory.holy_suit_experience_stored",
            CommandFamily.HolySuitTransferExperience =>
                "inventory.holy_suit_experience_transferred",
            CommandFamily.HolySuitConsumeWare =>
                "inventory.holy_suit_ware_consumed",
            CommandFamily.HolySuitTransformExperience =>
                "inventory.holy_suit_experience_transformed",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string ResultCode(HolySuitCommandResultStatus status) =>
        HolySuitNativeResults.IsCommitted(status)
            ? "committed"
            : "terminal_rejected";

    public static byte[] Encode(HolySuitExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(4_096);
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
            writer.WriteNumber("nativeResultSubId", receipt.NativeResultSubId);
            writer.WriteNumber("requestedExperience", receipt.RequestedExperience);
            writer.WriteNumber("requestedPrisms", receipt.RequestedPrisms);
            writer.WriteNumber("characterExperienceBefore", receipt.CharacterExperienceBefore);
            writer.WriteNumber("characterExperienceAfter", receipt.CharacterExperienceAfter);
            writer.WriteNumber("dailyStoredExperienceBefore", receipt.DailyStoredExperienceBefore);
            writer.WriteNumber("dailyStoredExperienceAfter", receipt.DailyStoredExperienceAfter);
            writer.WriteBoolean("battlePassDailyLimitExempt", receipt.BattlePassDailyLimitExempt);
            writer.WriteNumber("prismsCreated", receipt.PrismsCreated);
            writer.WriteNumber("prismsConsumed", receipt.PrismsConsumed);
            writer.WriteStartArray("mutations");
            foreach (var mutation in receipt.Mutations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("role", (byte)mutation.Role);
                writer.WriteNumber("kitBagSlot", mutation.KitBagSlot);
                writer.WriteNumber("itemId", mutation.ItemId);
                writer.WriteNumber("itemInstanceId", mutation.ItemInstanceId);
                writer.WriteString("beforeCompactItemState", mutation.BeforeCompactItemState);
                writer.WriteString("afterCompactItemState", mutation.AfterCompactItemState);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("progressionRevision", receipt.ProgressionRevision);
            writer.WriteNumber("inventoryRevision", receipt.InventoryRevision);
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

        EnsurePayloadBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static HolySuitExecutionReceipt Decode(ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var operation = (HolySuitCommandOperation)
            root.GetProperty("operation").GetByte();
        var family = (CommandFamily)root.GetProperty("family").GetUInt16();
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() != ContractVersion ||
            family != HolySuitCommandEnvelope.Family(operation))
        {
            throw new InvalidDataException(
                "The stored Holy Suit contract is unsupported.");
        }

        var mutationJson = root.GetProperty("mutations");
        if (mutationJson.ValueKind != JsonValueKind.Array ||
            mutationJson.GetArrayLength() > MaximumReceiptMutations)
        {
            throw new InvalidDataException(
                "The stored Holy Suit mutation list is invalid.");
        }

        var mutations = new List<HolySuitReceiptMutation>(
            mutationJson.GetArrayLength());
        foreach (var element in mutationJson.EnumerateArray())
        {
            mutations.Add(new HolySuitReceiptMutation(
                (HolySuitReceiptItemRole)element.GetProperty("role").GetByte(),
                element.GetProperty("kitBagSlot").GetInt32(),
                element.GetProperty("itemId").GetUInt32(),
                element.GetProperty("itemInstanceId").GetInt64(),
                element.GetProperty("beforeCompactItemState").GetString() ??
                    throw new InvalidDataException("Mutation before-state is missing."),
                element.GetProperty("afterCompactItemState").GetString() ??
                    throw new InvalidDataException("Mutation after-state is missing.")));
        }

        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored Holy Suit event ID is invalid.")
        };
        return new HolySuitExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            operation,
            root.GetProperty("npcId").GetInt32(),
            root.GetProperty("dialogIndex").GetInt32(),
            (HolySuitCommandResultStatus)root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            root.GetProperty("requestedExperience").GetInt64(),
            root.GetProperty("requestedPrisms").GetInt32(),
            root.GetProperty("characterExperienceBefore").GetInt64(),
            root.GetProperty("characterExperienceAfter").GetInt64(),
            root.GetProperty("dailyStoredExperienceBefore").GetInt64(),
            root.GetProperty("dailyStoredExperienceAfter").GetInt64(),
            root.GetProperty("battlePassDailyLimitExempt").GetBoolean(),
            root.GetProperty("prismsCreated").GetInt32(),
            root.GetProperty("prismsConsumed").GetInt32(),
            mutations,
            root.GetProperty("progressionRevision").GetInt64(),
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException("The stored result has no audit reference."),
            eventId);
    }

    public static HolySuitExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId,
        CommandFamily expectedFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        if (receipt.Family != expectedFamily ||
            !string.Equals(ResultCode(receipt.Status), expectedResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored Holy Suit result identity is inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException(
                "The stored Holy Suit result hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or > OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical Holy Suit result exceeds its bound.");
        }
    }
}
