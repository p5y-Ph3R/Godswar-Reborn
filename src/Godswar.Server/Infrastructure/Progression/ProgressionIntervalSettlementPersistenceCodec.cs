using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Progression;

namespace Godswar.Server.Infrastructure.Progression;

internal static class ProgressionIntervalSettlementPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string ConsumerKey = "progression_interval_v1";
    public const string AggregateType = "character_progression";
    public const string EventType = "progression.online_interval_settled";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "progression_interval_settlement";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:progression");

    public static byte[] Encode(
        ProgressionIntervalSettlementReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(1_024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteString(
                "onlineSessionId",
                receipt.OnlineSessionId);
            writer.WriteNumber(
                "intervalSequence",
                receipt.IntervalSequence);
            writer.WriteString(
                "onlineFromUtc",
                receipt.OnlineFromUtc);
            writer.WriteString(
                "onlineUntilUtc",
                receipt.OnlineUntilUtc);
            writer.WriteNumber(
                "gainedZodiacEnergyX100",
                receipt.GainedZodiacEnergyX100);
            writer.WriteBoolean(
                "zodiacCompensationApplied",
                receipt.ZodiacCompensationApplied);
            writer.WriteNumber(
                "updatedBoostCount",
                receipt.UpdatedBoostCount);
            WriteProjection(writer, receipt.Projection);
            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            writer.WriteString(
                "outboxEventId",
                receipt.OutboxEventId);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The progression interval result exceeds its bound.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static ProgressionIntervalSettlementReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored progression interval result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored progression interval contract is unsupported.");
        }

        return new ProgressionIntervalSettlementReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("onlineSessionId").GetGuid(),
            root.GetProperty("intervalSequence").GetInt64(),
            RequiredUtc(root, "onlineFromUtc"),
            RequiredUtc(root, "onlineUntilUtc"),
            root.GetProperty("gainedZodiacEnergyX100").GetInt32(),
            root.GetProperty("zodiacCompensationApplied").GetBoolean(),
            root.GetProperty("updatedBoostCount").GetInt32(),
            ReadProjection(root.GetProperty("projection")),
            RequiredString(root, "auditReference"),
            root.GetProperty("outboxEventId").GetGuid());
    }

    public static ProgressionIntervalSettlementReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var receipt = Decode(payload);
        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHash,
                actualHash) ||
            !string.Equals(
                expectedResultCode,
                ResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored progression interval evidence is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void WriteProjection(
        Utf8JsonWriter writer,
        ProgressionIntervalProjection projection)
    {
        writer.WritePropertyName("projection");
        writer.WriteStartObject();
        writer.WriteString(
            "onlineSessionId",
            projection.OnlineSessionId);
        writer.WriteNumber(
            "lastIntervalSequence",
            projection.LastIntervalSequence);
        writer.WriteString(
            "lastIntervalEndUtc",
            projection.LastIntervalEndUtc);
        writer.WriteNumber(
            "aggregateRevision",
            projection.AggregateRevision);
        writer.WriteNumber(
            "zodiacEnergy",
            projection.ZodiacEnergy);
        writer.WriteNumber(
            "zodiacEnergyRemainderX100",
            projection.ZodiacEnergyRemainderX100);
        writer.WriteString(
            "zodiacOnlineDay",
            projection.ZodiacOnlineDay.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture));
        writer.WriteNumber(
            "zodiacOnlineDurationTicksToday",
            projection.ZodiacOnlineDurationTicksToday);
        if (projection.ZodiacLastCompensationDay is { } compensationDay)
        {
            writer.WriteString(
                "zodiacLastCompensationDay",
                compensationDay.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull("zodiacLastCompensationDay");
        }

        writer.WriteEndObject();
    }

    private static ProgressionIntervalProjection ReadProjection(
        JsonElement root)
    {
        var compensation = root.GetProperty(
            "zodiacLastCompensationDay");
        return new ProgressionIntervalProjection(
            root.GetProperty("onlineSessionId").GetGuid(),
            root.GetProperty("lastIntervalSequence").GetInt64(),
            RequiredUtc(root, "lastIntervalEndUtc"),
            root.GetProperty("aggregateRevision").GetInt64(),
            root.GetProperty("zodiacEnergy").GetInt32(),
            root.GetProperty(
                "zodiacEnergyRemainderX100").GetInt32(),
            DateOnly.ParseExact(
                RequiredString(root, "zodiacOnlineDay"),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            root.GetProperty(
                "zodiacOnlineDurationTicksToday").GetInt64(),
            compensation.ValueKind == JsonValueKind.Null
                ? null
                : DateOnly.ParseExact(
                    compensation.GetString() ??
                        throw new InvalidDataException(
                            "The compensation day is invalid."),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset RequiredUtc(
        JsonElement root,
        string name)
    {
        var value = root.GetProperty(name).GetDateTimeOffset();
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"The stored {name} is not UTC.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement root,
        string name) =>
        root.GetProperty(name).GetString() ??
        throw new InvalidDataException(
            $"The stored progression interval has no {name}.");
}
