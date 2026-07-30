using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Zodiac;

internal static class ZodiacSkillGridSelectionPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode = "terminal_rejected";
    public const string ConsumerKey = "zodiac_grid_selection_v1";
    public const string AggregateType = "zodiac_grid_selection";
    public const string EventType = "zodiac.skill_grid_selected";
    public const string OrderingPolicy = "latest_wins";
    public const string CommandFamily = "zodiac_skill_grid_selection";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    private const int PropertyCount = 10;
    private const ushort AllPropertiesMask =
        (1 << PropertyCount) - 1;

    public static string CommandAggregateKey(int characterId)
    {
        EnsureCharacter(characterId);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:zodiac-skill-grids");
    }

    public static string EventAggregateKey(
        int characterId,
        int gridIndex)
    {
        EnsureCharacter(characterId);
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:zodiac-grid-selection:{gridIndex}");
    }

    public static string ResultCode(
        ZodiacSkillGridSelectionReceiptStatus status) =>
        status == ZodiacSkillGridSelectionReceiptStatus.Succeeded
            ? CommittedResultCode
            : Enum.IsDefined(status)
                ? TerminalRejectedResultCode
                : throw new ArgumentOutOfRangeException(nameof(status));

    public static byte[] Encode(
        ZodiacSkillGridSelectionExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber("gridIndex", receipt.GridIndex);
            writer.WriteNumber("currentLevel", receipt.CurrentLevel);
            writer.WriteNumber(
                "previousSkillKind",
                receipt.PreviousSkillKind);
            writer.WriteNumber(
                "selectedSkillKind",
                receipt.SelectedSkillKind);
            if (receipt.AggregateRevision is { } revision)
            {
                writer.WriteNumber("aggregateRevision", revision);
            }
            else
            {
                writer.WriteNull("aggregateRevision");
            }

            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            if (receipt.OutboxEventId is { } eventId)
            {
                writer.WriteString("outboxEventId", eventId);
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

    public static ZodiacSkillGridSelectionExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsureBound(payload.Length);
        try
        {
            using var document = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3
                });
            var root = document.RootElement;
            EnsureShape(root);
            if (root.GetProperty("contractVersion").GetInt16() !=
                ContractVersion)
            {
                throw new InvalidDataException(
                    "The Zodiac selection contract is unsupported.");
            }

            return new ZodiacSkillGridSelectionExecutionReceipt(
                root.GetProperty("characterId").GetInt32(),
                (ZodiacSkillGridSelectionReceiptStatus)
                    root.GetProperty("status").GetByte(),
                root.GetProperty("gridIndex").GetInt32(),
                root.GetProperty("currentLevel").GetByte(),
                root.GetProperty("previousSkillKind").GetInt32(),
                root.GetProperty("selectedSkillKind").GetInt32(),
                NullableInt64(root.GetProperty("aggregateRevision")),
                root.GetProperty("auditReference").GetString() ??
                    throw new InvalidDataException(
                        "The Zodiac selection audit is absent."),
                NullableGuid(root.GetProperty("outboxEventId")));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or
            InvalidOperationException or ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException(
                "The Zodiac selection evidence is malformed.",
                exception);
        }
    }

    public static ZodiacSkillGridSelectionExecutionReceipt
        DecodeAndVerify(
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
            expectedAuditId <= 0 ||
            receipt.AuditReference != expectedAuditId.ToString(
                CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException(
                "The Zodiac selection receipt identity is inconsistent.");
        }

        var actual = Hash(Encode(receipt));
        if (expectedHash.Length != actual.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expectedHash))
        {
            throw new InvalidDataException(
                "The Zodiac selection receipt hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void EnsureShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The Zodiac selection result must be an object.");
        }

        ushort found = 0;
        foreach (var property in root.EnumerateObject())
        {
            var bit = PropertyBit(property.Name);
            if ((found & bit) != 0)
            {
                throw new InvalidDataException(
                    "The Zodiac selection result has duplicate fields.");
            }

            found |= bit;
        }

        if (found != AllPropertiesMask)
        {
            throw new InvalidDataException(
                "The Zodiac selection result has missing fields.");
        }
    }

    private static ushort PropertyBit(string name) =>
        name switch
        {
            "contractVersion" => 1 << 0,
            "characterId" => 1 << 1,
            "status" => 1 << 2,
            "gridIndex" => 1 << 3,
            "currentLevel" => 1 << 4,
            "previousSkillKind" => 1 << 5,
            "selectedSkillKind" => 1 << 6,
            "aggregateRevision" => 1 << 7,
            "auditReference" => 1 << 8,
            "outboxEventId" => 1 << 9,
            _ => throw new InvalidDataException(
                "The Zodiac selection result has unknown fields.")
        };

    private static long? NullableInt64(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetInt64(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The Zodiac selection revision is invalid.")
        };

    private static Guid? NullableGuid(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetGuid(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException(
                "The Zodiac selection event ID is invalid.")
        };

    private static void EnsureBound(int bytes)
    {
        if (bytes is <= 0 or > OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The Zodiac selection result exceeds its bound.");
        }
    }

    private static void EnsureCharacter(int characterId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
    }
}
