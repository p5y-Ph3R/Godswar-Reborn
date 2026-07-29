using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Talents;

namespace Godswar.Server.Infrastructure.Talents;

internal static class TalentUpgradePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string ConsumerKey = "talent_projection_v1";
    public const string AggregateType = "character_talent";
    public const string EventType = "talent.upgraded";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "talent_upgrade";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    public static string AggregateKey(
        int characterId,
        int talentId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:talent:{talentId}");

    public static byte[] Encode(
        TalentUpgradeExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("talentId", receipt.TalentId);
            writer.WriteNumber("rank", receipt.Rank);
            writer.WriteNumber("cost", receipt.Cost);
            writer.WriteNumber(
                "remainingTalentPoints",
                receipt.RemainingTalentPoints);
            writer.WriteNumber("displayValue", receipt.DisplayValue);
            writer.WriteNumber(
                "aggregateRevision",
                receipt.AggregateRevision);
            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            writer.WriteString(
                "outboxEventId",
                receipt.OutboxEventId);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The canonical talent result exceeds its durable bound.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static TalentUpgradeExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored talent result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored talent result contract is unsupported.");
        }

        return new TalentUpgradeExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("talentId").GetInt32(),
            root.GetProperty("rank").GetInt32(),
            root.GetProperty("cost").GetInt32(),
            root.GetProperty("remainingTalentPoints").GetInt32(),
            root.GetProperty("displayValue").GetInt32(),
            root.GetProperty("aggregateRevision").GetInt64(),
            root.GetProperty("auditReference").GetString() ??
                throw new InvalidDataException(
                    "The stored talent result has no audit reference."),
            root.GetProperty("outboxEventId").GetGuid());
    }

    public static TalentUpgradeExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The stored talent result hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);
}
