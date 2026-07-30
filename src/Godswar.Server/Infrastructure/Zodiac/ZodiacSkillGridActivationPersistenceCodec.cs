using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.Infrastructure.Zodiac;

internal static class ZodiacSkillGridActivationPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string ConsumerKey = "zodiac_grid_activation_v1";
    public const string AggregateType = "zodiac_grid_activation";
    public const string EventType = "zodiac.skill_grid_activated";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "zodiac_skill_grid_activation";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";
    public const string LedgerReasonCode = "zodiac_skill_grid_activation";
    public const long AggregateRevision = 1;

    public static string AggregateKey(
        int characterId,
        int gridIndex) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:grid:{gridIndex}");

    public static byte[] Encode(
        ZodiacSkillGridActivationExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber("gridIndex", receipt.GridIndex);
            writer.WriteNumber("goldCost", receipt.GoldCost);
            writer.WriteNumber("goldBefore", receipt.GoldBefore);
            writer.WriteNumber("goldAfter", receipt.GoldAfter);
            writer.WriteNumber("currentLevel", receipt.CurrentLevel);
            writer.WriteNumber(
                "selectedSkillId",
                receipt.SelectedSkillId);
            writer.WriteNumber(
                "walletRevision",
                receipt.WalletRevision);
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
                "The canonical Zodiac activation result exceeds its bound.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static ZodiacSkillGridActivationExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored Zodiac activation result has an invalid size.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("contractVersion").GetInt32() !=
                ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Zodiac activation result is unsupported.");
        }

        return new ZodiacSkillGridActivationExecutionReceipt(
            root.GetProperty("characterId").GetInt32(),
            root.GetProperty("gridIndex").GetInt32(),
            root.GetProperty("goldCost").GetInt32(),
            root.GetProperty("goldBefore").GetInt32(),
            root.GetProperty("goldAfter").GetInt32(),
            root.GetProperty("currentLevel").GetByte(),
            root.GetProperty("selectedSkillId").GetInt32(),
            root.GetProperty("walletRevision").GetInt64(),
            RequiredString(root, "auditReference"),
            root.GetProperty("outboxEventId").GetGuid());
    }

    public static ZodiacSkillGridActivationExecutionReceipt
        DecodeAndVerify(
            string payloadJson,
            ReadOnlySpan<byte> expectedHash,
            string expectedResultCode,
            long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The stored Zodiac activation result has an invalid size.");
        }

        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        var canonical = Encode(receipt);
        var actualHash = SHA256.HashData(canonical);
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash) ||
            !string.Equals(
                expectedResultCode,
                ResultCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored Zodiac activation evidence is invalid.");
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
            $"The stored Zodiac activation result has no {name}.");
}
