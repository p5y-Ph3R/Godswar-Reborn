using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class ClassSuitPersistenceCodec
{
    public const short ContractVersion = 2;
    public const int MaximumMutationCount = 5;
    public const string ConsumerKey =
        DeveloperItemGrantPersistenceCodec.ConsumerKey;
    public const string AggregateType = "character_inventory";
    public const string PrincipalType = "account";
    public const string OrderingPolicy = "strict";
    public const string RetentionPolicy = "permanent";
    public const string EventType = "inventory.class_suit_changed";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static string FamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.ClassSuitExchangeTierI =>
                "class_suit_exchange_tier_i",
            CommandFamily.ClassSuitConvertToCommon =>
                "class_suit_convert_to_common",
            CommandFamily.ClassSuitUpgradeTierII =>
                "class_suit_upgrade_tier_ii",
            CommandFamily.ClassSuitUpgradeTierIII =>
                "class_suit_upgrade_tier_iii",
            CommandFamily.ClassSuitUpgradeTierIV =>
                "class_suit_upgrade_tier_iv",
            CommandFamily.ClassSuitAddAttribute =>
                "class_suit_add_attribute",
            CommandFamily.ClassSuitDeleteAttribute =>
                "class_suit_delete_attribute",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string ResultCode(ClassSuitCommandResultStatus status) =>
        status == ClassSuitCommandResultStatus.Succeeded
            ? "committed"
            : "terminal_rejected";

    public static byte[] Encode(ClassSuitExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateReceipt(receipt);
        var buffer = new ArrayBufferWriter<byte>(2_048);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("contractVersion", ContractVersion);
        writer.WriteNumber("family", (ushort)receipt.Family);
        writer.WriteNumber("characterId", receipt.CharacterId);
        writer.WriteNumber("operation", (short)receipt.Operation);
        writer.WriteNumber("npcId", receipt.NpcId);
        writer.WriteNumber("dialogIndex", receipt.DialogIndex);
        WriteReplayIntent(writer, receipt.ReplayIntent);
        writer.WriteNumber("status", (byte)receipt.Status);
        writer.WriteNumber("nativeResultSubId", receipt.NativeResultSubId);
        writer.WriteStartArray("mutations");
        foreach (var mutation in receipt.Mutations)
        {
            writer.WriteStartObject();
            if (mutation.Location != ClassSuitItemLocation.KitBag)
            {
                writer.WriteNumber("location", (byte)mutation.Location);
            }
            writer.WriteNumber("kitBagSlot", mutation.KitBagSlot);
            writer.WriteNumber("beforeItemId", mutation.BeforeItemId);
            writer.WriteNumber("afterItemId", mutation.AfterItemId);
            writer.WriteString(
                "beforeCompactItemState",
                mutation.BeforeCompactItemState);
            writer.WriteString(
                "afterCompactItemState",
                mutation.AfterCompactItemState);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
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
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static ClassSuitExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt16() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Class Suit result contract is unsupported.");
        }

        var operation = (ClassSuitCommandOperation)
            root.GetProperty("operation").GetInt16();
        var family = (CommandFamily)
            root.GetProperty("family").GetUInt16();
        if (family != ClassSuitCommandEnvelope.Family(operation))
        {
            throw new InvalidDataException(
                "The stored Class Suit family is inconsistent.");
        }

        var mutationArray = root.GetProperty("mutations");
        if (mutationArray.ValueKind != JsonValueKind.Array ||
            mutationArray.GetArrayLength() > MaximumMutationCount)
        {
            throw new InvalidDataException(
                "The stored Class Suit mutation evidence is invalid.");
        }

        var mutations = new List<ClassSuitReceiptMutation>(
            mutationArray.GetArrayLength());
        foreach (var mutation in mutationArray.EnumerateArray())
        {
            var location = mutation.TryGetProperty(
                    "location",
                    out var locationElement)
                ? (ClassSuitItemLocation)locationElement.GetByte()
                : ClassSuitItemLocation.KitBag;
            mutations.Add(new ClassSuitReceiptMutation(
                mutation.GetProperty("kitBagSlot").GetInt32(),
                mutation.GetProperty("beforeItemId").GetUInt32(),
                mutation.GetProperty("afterItemId").GetUInt32(),
                mutation.GetProperty("beforeCompactItemState")
                    .GetString() ?? string.Empty,
                mutation.GetProperty("afterCompactItemState")
                    .GetString() ?? string.Empty,
                location));
        }

        var eventElement = root.GetProperty("outboxEventId");
        Guid? eventId = eventElement.ValueKind switch
        {
            JsonValueKind.String => eventElement.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The stored Class Suit event identity is invalid.")
        };
        var replayIntent = ReadReplayIntent(
            root.GetProperty("replayIntent"));
        var receipt = new ClassSuitExecutionReceipt(
            family,
            root.GetProperty("characterId").GetInt32(),
            operation,
            root.GetProperty("npcId").GetInt32(),
            root.GetProperty("dialogIndex").GetInt32(),
            replayIntent,
            (ClassSuitCommandResultStatus)
                root.GetProperty("status").GetByte(),
            root.GetProperty("nativeResultSubId").GetInt32(),
            mutations,
            root.GetProperty("inventoryRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored Class Suit audit reference is missing."),
            eventId);
        ValidateReceipt(receipt);
        return receipt;
    }

    public static ClassSuitExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId,
        CommandFamily expectedFamily)
    {
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        var encoded = Encode(receipt);
        var actualHash = SHA256.HashData(encoded);
        if (receipt.Family != expectedFamily ||
            !string.Equals(
                ResultCode(receipt.Status),
                expectedResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHash,
                actualHash))
        {
            throw new InvalidDataException(
                "The stored Class Suit result evidence is inconsistent.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void WriteReplayIntent(
        Utf8JsonWriter writer,
        ClassSuitReplayIntent intent)
    {
        writer.WriteStartObject("replayIntent");
        writer.WriteNumber("operation", (short)intent.Operation);
        writer.WriteNumber("npcId", intent.NpcId);
        writer.WriteNumber("dialogIndex", intent.DialogIndex);
        if (intent.GearLocation != ClassSuitItemLocation.KitBag)
        {
            writer.WriteNumber(
                "gearLocation",
                (byte)intent.GearLocation);
        }
        writer.WriteNumber("gearKitBagSlot", intent.GearKitBagSlot);
        writer.WriteNumber(
            "primaryMaterialKitBagSlot",
            intent.PrimaryMaterialKitBagSlot);
        writer.WriteNumber(
            "secondaryMaterialKitBagSlot",
            intent.SecondaryMaterialKitBagSlot);
        writer.WriteEndObject();
    }

    private static ClassSuitReplayIntent ReadReplayIntent(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The stored Class Suit replay intent is invalid.");
        }
        var gearLocation = element.TryGetProperty(
                "gearLocation",
                out var locationElement)
            ? (ClassSuitItemLocation)locationElement.GetByte()
            : ClassSuitItemLocation.KitBag;
        if (!ClassSuitReplayIntent.TryCreate(
                (ClassSuitCommandOperation)
                    element.GetProperty("operation").GetInt16(),
                element.GetProperty("npcId").GetInt32(),
                element.GetProperty("dialogIndex").GetInt32(),
                gearLocation,
                element.GetProperty("gearKitBagSlot").GetInt32(),
                element.GetProperty("primaryMaterialKitBagSlot").GetInt32(),
                element.GetProperty("secondaryMaterialKitBagSlot").GetInt32(),
                out var intent))
        {
            throw new InvalidDataException(
                "The stored Class Suit replay intent is invalid.");
        }

        return intent;
    }

    private static void ValidateReceipt(ClassSuitExecutionReceipt receipt)
    {
        if (receipt.CharacterId <= 0 ||
            receipt.Family !=
                ClassSuitCommandEnvelope.Family(receipt.Operation) ||
            !ClassSuitCommandEnvelope.IsEndpoint(
                receipt.NpcId,
                receipt.DialogIndex) ||
            !receipt.ReplayIntent.IsValid ||
            receipt.ReplayIntent.Operation != receipt.Operation ||
            receipt.ReplayIntent.NpcId != receipt.NpcId ||
            receipt.ReplayIntent.DialogIndex != receipt.DialogIndex ||
            !Enum.IsDefined(receipt.Status) ||
            receipt.NativeResultSubId != ClassSuitNativeResults.Resolve(
                receipt.Operation,
                receipt.Status) ||
            receipt.InventoryRevision < 0 ||
            string.IsNullOrWhiteSpace(receipt.AuditReference))
        {
            throw new InvalidDataException(
                "The Class Suit receipt is invalid.");
        }

        var succeeded =
            receipt.Status == ClassSuitCommandResultStatus.Succeeded;
        if (succeeded != receipt.OutboxEventId.HasValue ||
            succeeded != (receipt.Mutations.Count > 0) ||
            receipt.Mutations.Count > MaximumMutationCount ||
            receipt.Mutations.Any(static value =>
                !Enum.IsDefined(value.Location) ||
                value.Location == ClassSuitItemLocation.KitBag &&
                value.KitBagSlot is
                    < ClassSuitCommandEnvelope.MinimumKitBagSlot or
                    > ClassSuitCommandEnvelope.MaximumKitBagSlot ||
                value.Location == ClassSuitItemLocation.Equipment &&
                value.KitBagSlot !=
                    ClassSuitCommandEnvelope.EquippedWeaponSlot) ||
            receipt.Mutations.Any(value =>
                value.Location == ClassSuitItemLocation.Equipment &&
                (receipt.ReplayIntent.GearLocation !=
                    ClassSuitItemLocation.Equipment ||
                 value.KitBagSlot !=
                    receipt.ReplayIntent.GearKitBagSlot)) ||
            succeeded && !receipt.Mutations.Any(value =>
                value.Location == receipt.ReplayIntent.GearLocation &&
                value.KitBagSlot ==
                    receipt.ReplayIntent.GearKitBagSlot) ||
            receipt.Mutations.Select(static value =>
                    (value.Location, value.KitBagSlot))
                .Distinct().Count() != receipt.Mutations.Count)
        {
            throw new InvalidDataException(
                "The Class Suit receipt mutation evidence is invalid.");
        }
    }
}
