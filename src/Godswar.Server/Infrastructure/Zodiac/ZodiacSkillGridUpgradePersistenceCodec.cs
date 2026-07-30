using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.Infrastructure.Zodiac;

internal static class ZodiacSkillGridUpgradePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode = "terminal_rejected";
    public const string ConsumerKey = "zodiac_grid_upgrade_v1";
    public const string AggregateType = "zodiac_grid_upgrade";
    public const string CommandAggregateType = AggregateType;
    public const string EventAggregateType = AggregateType;
    public const string EventType = "zodiac.skill_grid_upgraded";
    public const string OrderingPolicy = "latest_wins";
    public const string CommandFamily = "zodiac_skill_grid_upgrade";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    private const int PropertyCount = 19;
    private const uint AllPropertiesMask = (1u << PropertyCount) - 1u;

    public static string CommandAggregateKey(int characterId)
    {
        EnsureCharacterId(characterId);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:zodiac-skill-grids");
    }

    public static string EventAggregateKey(
        int characterId,
        int gridIndex)
    {
        EnsureCharacterId(characterId);
        if (gridIndex is
            < ZodiacSkillGridUpgradeCommandEnvelope.MinimumGridIndex or
            > ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:zodiac-grid:{gridIndex}");
    }

    public static string ResultCode(
        ZodiacSkillGridUpgradeReceiptStatus status) =>
        status switch
        {
            ZodiacSkillGridUpgradeReceiptStatus.Succeeded =>
                CommittedResultCode,
            ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid or
            ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached or
            ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow or
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy or
            ZodiacSkillGridUpgradeReceiptStatus
                .InsufficientTalentPoints =>
                TerminalRejectedResultCode,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static byte[] Encode(
        ZodiacSkillGridUpgradeExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(768);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber("gridIndex", receipt.GridIndex);
            writer.WriteNumber("previousLevel", receipt.PreviousLevel);
            writer.WriteNumber("currentLevel", receipt.CurrentLevel);
            writer.WriteNumber(
                "currentZodiacLevel",
                receipt.CurrentZodiacLevel);
            writer.WriteNumber(
                "requiredZodiacLevel",
                receipt.RequiredZodiacLevel);
            writer.WriteNumber("energyCost", receipt.EnergyCost);
            writer.WriteNumber("energyBefore", receipt.EnergyBefore);
            writer.WriteNumber(
                "energyRemainderBeforeX100",
                receipt.EnergyRemainderBeforeX100);
            writer.WriteNumber("energyAfter", receipt.EnergyAfter);
            writer.WriteNumber(
                "energyRemainderAfterX100",
                receipt.EnergyRemainderAfterX100);
            writer.WriteNumber(
                "talentPointCost",
                receipt.TalentPointCost);
            writer.WriteNumber(
                "talentPointsBefore",
                receipt.TalentPointsBefore);
            writer.WriteNumber(
                "talentPointsAfter",
                receipt.TalentPointsAfter);
            writer.WriteNumber(
                "selectedSkillId",
                receipt.SelectedSkillId);
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

        EnsurePayloadBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static ZodiacSkillGridUpgradeExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        try
        {
            using var document = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            var root = document.RootElement;
            EnsureExactShape(root);
            if (root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
            {
                throw new InvalidDataException(
                    "The stored Zodiac upgrade contract is unsupported.");
            }

            var eventElement = root.GetProperty("outboxEventId");
            Guid? eventId = eventElement.ValueKind switch
            {
                JsonValueKind.String => eventElement.GetGuid(),
                JsonValueKind.Null => null,
                _ => throw new InvalidDataException(
                    "The stored Zodiac upgrade event ID is invalid.")
            };

            return new ZodiacSkillGridUpgradeExecutionReceipt(
                root.GetProperty("characterId").GetInt32(),
                (ZodiacSkillGridUpgradeReceiptStatus)
                    root.GetProperty("status").GetByte(),
                root.GetProperty("gridIndex").GetInt32(),
                root.GetProperty("previousLevel").GetByte(),
                root.GetProperty("currentLevel").GetByte(),
                root.GetProperty("currentZodiacLevel").GetByte(),
                root.GetProperty("requiredZodiacLevel").GetByte(),
                root.GetProperty("energyCost").GetInt32(),
                root.GetProperty("energyBefore").GetInt32(),
                root.GetProperty("energyRemainderBeforeX100").GetInt32(),
                root.GetProperty("energyAfter").GetInt32(),
                root.GetProperty("energyRemainderAfterX100").GetInt32(),
                root.GetProperty("talentPointCost").GetInt32(),
                root.GetProperty("talentPointsBefore").GetInt32(),
                root.GetProperty("talentPointsAfter").GetInt32(),
                root.GetProperty("selectedSkillId").GetInt32(),
                RequiredString(root, "auditReference"),
                eventId);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            FormatException or
            InvalidOperationException or
            ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade evidence is malformed.",
                exception);
        }
    }

    public static ZodiacSkillGridUpgradeExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var payloadByteCount = Encoding.UTF8.GetByteCount(payloadJson);
        EnsurePayloadBound(payloadByteCount);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        if (!string.Equals(
                ResultCode(receipt.Status),
                expectedResultCode,
                StringComparison.Ordinal) ||
            expectedAuditId <= 0 ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade identity is inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void EnsureExactShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade result must be an object.");
        }

        uint found = 0;
        foreach (var property in root.EnumerateObject())
        {
            var bit = PropertyBit(property.Name);
            if ((found & bit) != 0)
            {
                throw new InvalidDataException(
                    "The stored Zodiac upgrade result has duplicate fields.");
            }

            found |= bit;
        }

        if (found != AllPropertiesMask)
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade result has missing fields.");
        }
    }

    private static uint PropertyBit(string name) =>
        name switch
        {
            "contractVersion" => 1u << 0,
            "characterId" => 1u << 1,
            "status" => 1u << 2,
            "gridIndex" => 1u << 3,
            "previousLevel" => 1u << 4,
            "currentLevel" => 1u << 5,
            "currentZodiacLevel" => 1u << 6,
            "requiredZodiacLevel" => 1u << 7,
            "energyCost" => 1u << 8,
            "energyBefore" => 1u << 9,
            "energyRemainderBeforeX100" => 1u << 10,
            "energyAfter" => 1u << 11,
            "energyRemainderAfterX100" => 1u << 12,
            "talentPointCost" => 1u << 13,
            "talentPointsBefore" => 1u << 14,
            "talentPointsAfter" => 1u << 15,
            "selectedSkillId" => 1u << 16,
            "auditReference" => 1u << 17,
            "outboxEventId" => 1u << 18,
            _ => throw new InvalidDataException(
                "The stored Zodiac upgrade result has unknown fields.")
        };

    private static string RequiredString(
        JsonElement root,
        string propertyName) =>
        root.GetProperty(propertyName).GetString() ??
        throw new InvalidDataException(
            $"The stored Zodiac upgrade result has no {propertyName}.");

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade result exceeds its bound.");
        }
    }

    private static void EnsureCharacterId(int characterId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
    }
}
